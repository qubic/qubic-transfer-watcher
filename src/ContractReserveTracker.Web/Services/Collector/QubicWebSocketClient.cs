using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ContractReserveTracker.Shared.Data;
using ContractReserveTracker.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace ContractReserveTracker.Web.Services.Collector;

/// <summary>
/// WebSocket client for connecting to Qubic Bob RPC log stream with multi-URL failover support.
/// Uses the new JSON-RPC 2.0 interface with qubic_subscribe method.
/// </summary>
public class QubicWebSocketClient : IDisposable
{
    private readonly Serilog.ILogger _log = Log.ForContext<QubicWebSocketClient>();
    private readonly List<string> _webSocketUrls;
    private readonly EventProcessor _eventProcessor;
    private readonly IDbContextFactory<ReserveDbContext> _dbContextFactory;
    private readonly int _reconnectDelaySeconds;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _disposed;
    private long _lastProcessedLogId = -1;
    private long _lastSeenTick;
    private int _currentEpoch;
    private int _currentUrlIndex;
    private string? _subscriptionId;
    private int _rpcRequestId = 1;

    public event EventHandler<string>? MessageReceived;
    public event EventHandler<Exception>? ErrorOccurred;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;

    public QubicWebSocketClient(
        List<string> webSocketUrls,
        EventProcessor eventProcessor,
        IDbContextFactory<ReserveDbContext> dbContextFactory,
        int reconnectDelaySeconds = 5)
    {
        if (webSocketUrls == null || webSocketUrls.Count == 0)
            throw new ArgumentException("At least one WebSocket URL is required", nameof(webSocketUrls));

        _webSocketUrls = webSocketUrls;
        _eventProcessor = eventProcessor;
        _dbContextFactory = dbContextFactory;
        _reconnectDelaySeconds = reconnectDelaySeconds;
        _currentUrlIndex = 0;
    }

    private string CurrentUrl => _webSocketUrls[_currentUrlIndex];

    private void SwitchToNextUrl()
    {
        var previousUrl = CurrentUrl;
        _currentUrlIndex = (_currentUrlIndex + 1) % _webSocketUrls.Count;
        _log.Warning("Switching from {PreviousUrl} to {NextUrl}", previousUrl, CurrentUrl);
    }

    private async Task LoadLatestEpochFromDbAsync()
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            // Find the latest epoch from LogProgress table
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

    public async Task UpdateProgressAsync(long logId, long tick, int epoch)
    {
        var epochChanged = epoch != _currentEpoch && epoch > 0;

        if (epochChanged)
        {
            // Save progress for old epoch before switching
            if (_currentEpoch > 0)
            {
                await SaveProgressToDbAsync();
            }

            _currentEpoch = epoch;
            // Load progress for new epoch
            await LoadProgressFromDbAsync(epoch);
        }

        if (logId > _lastProcessedLogId)
        {
            _lastProcessedLogId = logId;
        }

        if (tick > _lastSeenTick)
        {
            _lastSeenTick = tick;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isRunning = true;

        _log.Information("Starting WebSocket client with {UrlCount} URLs...", _webSocketUrls.Count);

        while (_isRunning && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReceiveAsync(_cts.Token);
            }
            catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
            {
                break;
            }
            catch (ServerBehindException ex)
            {
                _log.Warning("Server {Url} is behind: {Message}", CurrentUrl, ex.Message);
                SwitchToNextUrl();
                _log.Information("Reconnecting to {Url} in {DelaySeconds} seconds...", CurrentUrl, _reconnectDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(_reconnectDelaySeconds), _cts.Token);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "WebSocket error on {Url}", CurrentUrl);
                ErrorOccurred?.Invoke(this, ex);
                SwitchToNextUrl();

                if (_isRunning && !_cts.Token.IsCancellationRequested)
                {
                    _log.Information("Reconnecting to {Url} in {DelaySeconds} seconds...", CurrentUrl, _reconnectDelaySeconds);
                    await Task.Delay(TimeSpan.FromSeconds(_reconnectDelaySeconds), _cts.Token);
                }
            }
        }

        // Save progress on shutdown
        await SaveProgressToDbAsync();
        _log.Information("WebSocket client stopped");
    }

    private async Task ConnectAndReceiveAsync(CancellationToken cancellationToken)
    {
        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();
        _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        _log.Information("Connecting to {WebSocketUrl}...", CurrentUrl);

        try
        {
            await _webSocket.ConnectAsync(new Uri(CurrentUrl), cancellationToken);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Connection failed to {WebSocketUrl}", CurrentUrl);
            throw;
        }

        _log.Information("Connected to WebSocket!");

        // Load progress from database if we have previous data
        // On fresh start, _currentEpoch stays 0 and will be set from incoming log entries
        if (_currentEpoch <= 0)
        {
            await LoadLatestEpochFromDbAsync();
        }

        Connected?.Invoke(this, EventArgs.Empty);
        await SendSubscriptionAsync(cancellationToken);

        var buffer = new byte[8192];
        var messageBuilder = new StringBuilder();
        var saveCounter = 0;

        while (_webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _log.Warning("Server closed connection: {CloseReason}", result.CloseStatusDescription);
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
                    Disconnected?.Invoke(this, EventArgs.Empty);
                    break;
                }

                var messageChunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                messageBuilder.Append(messageChunk);

                if (result.EndOfMessage)
                {
                    var message = messageBuilder.ToString();
                    messageBuilder.Clear();

                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        MessageReceived?.Invoke(this, message);
                        await ProcessMessageAsync(message);

                        // Save progress periodically (every 100 messages)
                        saveCounter++;
                        if (saveCounter >= 100)
                        {
                            await SaveProgressToDbAsync();
                            saveCounter = 0;
                        }
                    }
                }
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                _log.Warning(ex, "Connection closed prematurely");
                Disconnected?.Invoke(this, EventArgs.Empty);
                break;
            }
        }

        // Save progress when disconnecting
        await SaveProgressToDbAsync();
    }

    private async Task SendSubscriptionAsync(CancellationToken cancellationToken)
    {
        // JSON-RPC 2.0 format for qubic_subscribe to logs
        // Log types: 8 = BURNING, 13 = CONTRACT_RESERVE_DEDUCTION
        // startLogId: resume from last processed log ID (0 = start from beginning)
        var startLogId = _lastProcessedLogId > 0 ? _lastProcessedLogId : 0;

        var subscribeMessage = new
        {
            jsonrpc = "2.0",
            method = "qubic_subscribe",
            @params = new object[]
            {
                "logs",
                new
                {
                    logType = new[] { QubicLogTypes.Burning, QubicLogTypes.ContractReserveDeduction },
                    startLogId
                }
            },
            id = _rpcRequestId++
        };

        var json = JsonSerializer.Serialize(subscribeMessage);

        _log.Information("Sending JSON-RPC subscription for log types [8, 13] with startLogId={StartLogId} (epoch: {Epoch})...",
            startLogId, _currentEpoch);

        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket!.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            cancellationToken);

        // Wait for subscription confirmation
        await WaitForSubscriptionConfirmationAsync(cancellationToken);
    }

    private async Task WaitForSubscriptionConfirmationAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var messageBuilder = new StringBuilder();

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            while (_webSocket!.State == WebSocketState.Open)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), linkedCts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new Exception("Server closed connection during subscription");
                }

                var messageChunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                messageBuilder.Append(messageChunk);

                if (result.EndOfMessage)
                {
                    var message = messageBuilder.ToString();
                    using var doc = JsonDocument.Parse(message);
                    var root = doc.RootElement;

                    // Check for subscription confirmation: {"jsonrpc":"2.0","result":"qubic_sub_0","id":1}
                    if (root.TryGetProperty("result", out var resultElement) && resultElement.ValueKind == JsonValueKind.String)
                    {
                        _subscriptionId = resultElement.GetString();
                        _log.Information("Subscription confirmed: {SubscriptionId}", _subscriptionId);
                        return;
                    }

                    // Check for error response
                    if (root.TryGetProperty("error", out var errorElement))
                    {
                        var errorMsg = errorElement.TryGetProperty("message", out var msgElement)
                            ? msgElement.GetString()
                            : "Unknown error";
                        throw new Exception($"Subscription failed: {errorMsg}");
                    }

                    // If it's already a notification, process it and continue
                    if (root.TryGetProperty("method", out var methodElement) &&
                        methodElement.GetString() == "qubic_subscription")
                    {
                        _log.Debug("Received notification before confirmation, processing...");
                        await ProcessMessageAsync(message);
                    }

                    messageBuilder.Clear();
                }
            }
        }
        catch (OperationCanceledException)
        {
            _log.Warning("Timeout waiting for subscription confirmation");
            throw new Exception("Subscription confirmation timeout");
        }
    }

    private async Task ProcessMessageAsync(string message)
    {
        try
        {
            var (logId, tick, epoch) = await _eventProcessor.ProcessMessageAsync(message);
            if (logId > 0 || tick > 0)
            {
                await UpdateProgressAsync(logId, tick, epoch);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error processing message");
        }
    }

    public async Task StopAsync()
    {
        _isRunning = false;
        _cts?.Cancel();

        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutting down", CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error closing WebSocket");
            }
        }

        await SaveProgressToDbAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _isRunning = false;
        // Note: Can't call async SaveProgressToDbAsync here, rely on StopAsync being called
        _cts?.Cancel();
        _cts?.Dispose();
        _webSocket?.Dispose();
    }
}

public class ServerBehindException : Exception
{
    public ServerBehindException(string message) : base(message) { }
}
