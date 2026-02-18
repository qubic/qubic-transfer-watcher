using Qubic.Bob;
using Qubic.Bob.Models;
using QubicTransferWatcher.Models;
using Serilog;

namespace QubicTransferWatcher.Services;

/// <summary>
/// Watches for Qubic log events (transfers and burns) using BobWebSocketClient
/// with file-based progress persistence.
/// </summary>
public class QubicLogWatcher : IAsyncDisposable
{
    private readonly ILogger _log = Log.ForContext<QubicLogWatcher>();
    private readonly BobWebSocketClient _bobClient;
    private readonly EventProcessor _eventProcessor;
    private readonly string _logIdFilePath;
    private readonly string _tickFilePath;
    private readonly string _epochFilePath;
    private long _lastProcessedLogId = -1;
    private long _lastSeenTick;
    private int _currentEpoch;

    public QubicLogWatcher(
        BobWebSocketOptions bobOptions,
        EventProcessor eventProcessor,
        string? dataDir = null)
    {
        _bobClient = new BobWebSocketClient(bobOptions);
        _eventProcessor = eventProcessor;

        dataDir ??= Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        _logIdFilePath = Path.Combine(dataDir, "last_logid.txt");
        _tickFilePath = Path.Combine(dataDir, "last_tick.txt");
        _epochFilePath = Path.Combine(dataDir, "last_epoch.txt");

        LoadProgress();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _log.Information("Connecting to Bob...");
        await _bobClient.ConnectAsync(cancellationToken);

        var options = new LogSubscriptionOptions
        {
            LogTypes = new List<int> { QubicLogTypes.QuTransfer, QubicLogTypes.Burning },
            StartLogId = _lastProcessedLogId >= 0 ? _lastProcessedLogId + 1 : 0
        };

        if (_currentEpoch > 0)
            options.StartEpoch = (uint)_currentEpoch;

        _log.Information("Subscribing to logs (startLogId: {StartLogId}, epoch: {Epoch})...",
            options.StartLogId, options.StartEpoch?.ToString() ?? "latest");

        var subscription = await _bobClient.SubscribeLogsAsync(options, cancellationToken);

        await foreach (var notification in subscription.WithCancellation(cancellationToken))
        {
            if (notification.CatchUpComplete)
            {
                _log.Information("Catch-up complete");
                continue;
            }

            // Detect epoch change
            if (notification.Epoch > 0 && (int)notification.Epoch != _currentEpoch && _currentEpoch > 0)
            {
                _log.Information("Epoch change detected: {OldEpoch} -> {NewEpoch}. Resetting logId from {OldLogId} to -1",
                    _currentEpoch, notification.Epoch, _lastProcessedLogId);
                _currentEpoch = (int)notification.Epoch;
                _lastProcessedLogId = -1;
                _lastSeenTick = 0;
                SaveProgress();
            }
            else if (_currentEpoch == 0 && notification.Epoch > 0)
            {
                _currentEpoch = (int)notification.Epoch;
                SaveEpoch();
            }

            await _eventProcessor.ProcessNotificationAsync(notification);
            UpdateProgress(notification.LogId, notification.Tick, (int)notification.Epoch);
        }
    }

    private void UpdateProgress(long logId, uint tick, int epoch)
    {
        bool changed = false;

        if (logId > _lastProcessedLogId)
        {
            _lastProcessedLogId = logId;
            changed = true;
        }

        if (tick > (uint)_lastSeenTick)
        {
            _lastSeenTick = tick;
            changed = true;
        }

        if (changed)
            SaveProgress();
    }

    private void LoadProgress()
    {
        try
        {
            if (File.Exists(_epochFilePath))
            {
                var content = File.ReadAllText(_epochFilePath).Trim();
                if (int.TryParse(content, out var epoch) && epoch > 0)
                {
                    _currentEpoch = epoch;
                    _log.Information("Loaded last epoch from file: {LastEpoch}", _currentEpoch);
                }
            }

            if (File.Exists(_logIdFilePath))
            {
                var content = File.ReadAllText(_logIdFilePath).Trim();
                if (long.TryParse(content, out var logId) && logId >= 0)
                {
                    _lastProcessedLogId = logId;
                    _log.Information("Loaded last logId from file: {LastLogId}", _lastProcessedLogId);
                }
            }

            if (File.Exists(_tickFilePath))
            {
                var content = File.ReadAllText(_tickFilePath).Trim();
                if (long.TryParse(content, out var tick) && tick > 0)
                {
                    _lastSeenTick = tick;
                    _log.Information("Loaded last tick from file: {LastTick}", _lastSeenTick);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error loading progress");
        }
    }

    private void SaveProgress()
    {
        try
        {
            if (_lastProcessedLogId > 0)
                File.WriteAllText(_logIdFilePath, _lastProcessedLogId.ToString());

            if (_lastSeenTick > 0)
                File.WriteAllText(_tickFilePath, (_lastSeenTick - 1).ToString());

            SaveEpoch();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error saving progress");
        }
    }

    private void SaveEpoch()
    {
        try
        {
            if (_currentEpoch > 0)
                File.WriteAllText(_epochFilePath, _currentEpoch.ToString());
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error saving epoch");
        }
    }

    public async ValueTask DisposeAsync()
    {
        SaveProgress();
        await _bobClient.DisposeAsync();
    }
}
