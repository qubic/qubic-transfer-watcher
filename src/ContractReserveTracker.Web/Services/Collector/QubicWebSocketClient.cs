using ContractReserveTracker.Shared.Data;
using ContractReserveTracker.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Qubic.Bob;
using Qubic.Bob.Models;
using Serilog;

namespace ContractReserveTracker.Web.Services.Collector;

/// <summary>
/// Collects Qubic log events (burns and reserve deductions) using BobWebSocketClient
/// with database-based progress persistence.
/// </summary>
public class QubicLogCollector : IAsyncDisposable
{
    private readonly Serilog.ILogger _log = Log.ForContext<QubicLogCollector>();
    private readonly BobWebSocketOptions _bobOptions;
    private BobWebSocketClient _bobClient;
    private readonly EventProcessor _eventProcessor;
    private readonly IDbContextFactory<ReserveDbContext> _dbContextFactory;
    private long _lastProcessedLogId = -1;
    private long _lastSeenTick;
    private int _currentEpoch;

    public QubicLogCollector(
        BobWebSocketOptions bobOptions,
        EventProcessor eventProcessor,
        IDbContextFactory<ReserveDbContext> dbContextFactory)
    {
        _bobOptions = bobOptions;
        _bobClient = new BobWebSocketClient(bobOptions);
        _eventProcessor = eventProcessor;
        _dbContextFactory = dbContextFactory;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await LoadLatestEpochFromDbAsync();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _log.Information("Connecting to Bob...");
                await _bobClient.ConnectAsync(cancellationToken);

                // Check if epoch changed since last run
                try
                {
                    var epochInfo = await _bobClient.GetCurrentEpochAsync(cancellationToken);
                    var currentEpoch = (int)epochInfo.Epoch;
                    if (_currentEpoch > 0 && currentEpoch != _currentEpoch)
                    {
                        _log.Information("Epoch changed since last run: {OldEpoch} -> {NewEpoch}. Resetting logId from {OldLogId} to -1",
                            _currentEpoch, currentEpoch, _lastProcessedLogId);
                        await SaveProgressToDbAsync();
                        _currentEpoch = currentEpoch;
                        _lastProcessedLogId = -1;
                        _lastSeenTick = 0;
                        await LoadProgressFromDbAsync(_currentEpoch);
                    }
                    else if (_currentEpoch == 0 && currentEpoch > 0)
                    {
                        _currentEpoch = currentEpoch;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warning(ex, "Failed to check current epoch, proceeding with saved state");
                }

                var options = new LogSubscriptionOptions
                {
                    LogTypes = new List<int> { QubicLogTypes.Burning, QubicLogTypes.ContractReserveDeduction },
                };

                // Only set startLogId if we have a saved position; otherwise let server start from current
                if (_lastProcessedLogId >= 0)
                    options.StartLogId = _lastProcessedLogId + 1;

                if (_currentEpoch > 0)
                    options.StartEpoch = (uint)_currentEpoch;

                _log.Information("Subscribing to logs (startLogId: {StartLogId}, epoch: {Epoch})...",
                    options.StartLogId?.ToString() ?? "latest", options.StartEpoch?.ToString() ?? "latest");

                var subscription = await _bobClient.SubscribeLogsAsync(options, cancellationToken);
                int saveCounter = 0;
                bool inCatchUp = false;
                DateTime catchUpStartedAt = DateTime.MinValue;
                DateTime lastProgressLogAt = DateTime.MinValue;
                long catchUpMatchedAtStart = 0;

                await foreach (var notification in subscription.WithCancellation(cancellationToken))
                {
                    // Detect catch-up start (first notification with IsCatchUp set)
                    if (notification.IsCatchUp && !inCatchUp && !notification.CatchUpComplete)
                    {
                        inCatchUp = true;
                        catchUpStartedAt = DateTime.UtcNow;
                        lastProgressLogAt = catchUpStartedAt;
                        _log.Information(
                            "Catch-up started (epoch={Epoch}, fromLogId={FromLogId}, total={Total})",
                            options.StartEpoch?.ToString() ?? "?",
                            options.StartLogId?.ToString() ?? "?",
                            notification.Total?.ToString() ?? "?");
                    }

                    // Throttled progress logging for CatchUpProgress pings (no log body, just stats)
                    if (notification.CatchUpProgress)
                    {
                        var now = DateTime.UtcNow;
                        if ((now - lastProgressLogAt).TotalSeconds >= 5)
                        {
                            var elapsed = (now - catchUpStartedAt).TotalSeconds;
                            var matched = notification.Matched ?? notification.TotalMatched ?? 0;
                            var rate = elapsed > 0 ? (matched - catchUpMatchedAtStart) / elapsed : 0;
                            _log.Information(
                                "Catch-up progress: {Percent:F1}% ({Current}/{Total}), position={Position}, matched={Matched}, processed={Processed}, rate={Rate:F0}/s",
                                notification.Percent ?? 0,
                                notification.Current?.ToString() ?? "?",
                                notification.Total?.ToString() ?? "?",
                                notification.Position?.ToString() ?? "?",
                                matched,
                                notification.TotalProcessed?.ToString() ?? "?",
                                rate);
                            lastProgressLogAt = now;
                        }
                        continue;
                    }

                    if (notification.CatchUpComplete)
                    {
                        if (inCatchUp)
                        {
                            var elapsed = (DateTime.UtcNow - catchUpStartedAt).TotalSeconds;
                            _log.Information(
                                "Catch-up complete in {Elapsed:F1}s (lastLogId={LastLogId}, lastTick={LastTick}, totalMatched={Matched}, totalProcessed={Processed})",
                                elapsed,
                                _lastProcessedLogId,
                                _lastSeenTick,
                                notification.TotalMatched?.ToString() ?? "?",
                                notification.TotalProcessed?.ToString() ?? "?");
                        }
                        else
                        {
                            _log.Information("Catch-up complete");
                        }
                        inCatchUp = false;
                        await SaveProgressToDbAsync();
                        continue;
                    }

                    // Detect epoch change
                    if (notification.Epoch > 0 && (int)notification.Epoch != _currentEpoch && _currentEpoch > 0)
                    {
                        _log.Information("Epoch change detected: {OldEpoch} -> {NewEpoch}. Resetting logId from {OldLogId} to -1",
                            _currentEpoch, notification.Epoch, _lastProcessedLogId);

                        // Save progress for old epoch before switching
                        await SaveProgressToDbAsync();

                        _currentEpoch = (int)notification.Epoch;
                        _lastProcessedLogId = -1;
                        _lastSeenTick = 0;

                        // Load progress for new epoch (if we have any saved data for it)
                        await LoadProgressFromDbAsync(_currentEpoch);
                    }
                    else if (_currentEpoch == 0 && notification.Epoch > 0)
                    {
                        _currentEpoch = (int)notification.Epoch;
                    }

                    await _eventProcessor.ProcessNotificationAsync(notification);

                    if (notification.LogId > _lastProcessedLogId)
                        _lastProcessedLogId = notification.LogId;

                    if (notification.Tick > (uint)_lastSeenTick)
                        _lastSeenTick = notification.Tick;

                    // Heartbeat during catch-up when server doesn't send CatchUpProgress pings
                    if (notification.IsCatchUp && inCatchUp)
                    {
                        var now = DateTime.UtcNow;
                        if ((now - lastProgressLogAt).TotalSeconds >= 5)
                        {
                            _log.Information(
                                "Catch-up in progress: logId={LogId}, tick={Tick}, epoch={Epoch}",
                                _lastProcessedLogId, _lastSeenTick, _currentEpoch);
                            lastProgressLogAt = now;
                        }
                    }

                    // Save progress periodically (every 100 messages)
                    if (++saveCounter >= 100)
                    {
                        await SaveProgressToDbAsync();
                        saveCounter = 0;
                    }
                }

                await SaveProgressToDbAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Connection error, retrying in 5 seconds...");
                await SaveProgressToDbAsync();
                await _bobClient.DisposeAsync();
                _bobClient = new BobWebSocketClient(_bobOptions);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        await SaveProgressToDbAsync();
    }

    private async Task LoadLatestEpochFromDbAsync()
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var latestProgress = await db.LogProgress
                .OrderByDescending(p => p.Epoch)
                .FirstOrDefaultAsync();

            if (latestProgress != null)
            {
                _currentEpoch = latestProgress.Epoch;
                _lastProcessedLogId = latestProgress.LastLogId;
                _lastSeenTick = latestProgress.LastTick;
                _log.Information("Loaded latest progress: epoch={Epoch}, lastLogId={LastLogId}, lastTick={LastTick}",
                    _currentEpoch, _lastProcessedLogId, _lastSeenTick);
            }
            else
            {
                _log.Information("No previous progress found, starting fresh");
                _lastProcessedLogId = -1;
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error loading latest epoch from database");
        }
    }

    private async Task LoadProgressFromDbAsync(int epoch)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            var progress = await db.LogProgress.FindAsync(epoch);

            if (progress != null)
            {
                _lastProcessedLogId = progress.LastLogId;
                _lastSeenTick = progress.LastTick;
                _log.Information("Loaded progress for epoch {Epoch}: lastLogId={LastLogId}, lastTick={LastTick}",
                    epoch, _lastProcessedLogId, _lastSeenTick);
            }
            else
            {
                // Try to get the highest logId from events in this epoch
                var maxBurnLogId = await db.BurnEvents
                    .Where(e => e.Epoch == epoch)
                    .MaxAsync(e => (long?)e.LogId) ?? -1;

                var maxDeductLogId = await db.DeductEvents
                    .Where(e => e.Epoch == epoch)
                    .MaxAsync(e => (long?)e.LogId) ?? -1;

                _lastProcessedLogId = Math.Max(maxBurnLogId, maxDeductLogId);

                if (_lastProcessedLogId > 0)
                {
                    _log.Information("Recovered lastLogId from events for epoch {Epoch}: {LastLogId}",
                        epoch, _lastProcessedLogId);
                }
                else
                {
                    _lastProcessedLogId = -1;
                    _log.Information("No previous progress for epoch {Epoch}, starting fresh", epoch);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error loading progress from database");
        }
    }

    private async Task SaveProgressToDbAsync()
    {
        if (_currentEpoch <= 0 || _lastProcessedLogId <= 0)
            return;

        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            var progress = await db.LogProgress.FindAsync(_currentEpoch);

            if (progress != null)
            {
                progress.LastLogId = _lastProcessedLogId;
                progress.LastTick = _lastSeenTick;
                progress.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                db.LogProgress.Add(new LogProgress
                {
                    Epoch = _currentEpoch,
                    LastLogId = _lastProcessedLogId,
                    LastTick = _lastSeenTick,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error saving progress to database");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await SaveProgressToDbAsync();
        await _bobClient.DisposeAsync();
    }
}
