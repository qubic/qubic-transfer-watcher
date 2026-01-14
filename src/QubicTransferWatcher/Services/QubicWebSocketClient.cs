using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using QubicTransferWatcher.Models;
using Serilog;

namespace QubicTransferWatcher.Services;

/// <summary>
/// WebSocket client for connecting to Qubic log stream with multi-URL failover support
/// </summary>
public class QubicWebSocketClient : IDisposable
{
    private readonly ILogger _log = Log.ForContext<QubicWebSocketClient>();
    private readonly List<string> _webSocketUrls;
    private readonly EventProcessor _eventProcessor;
    private readonly int _reconnectDelaySeconds;
    private readonly string _logIdFilePath;
    private readonly string _tickFilePath;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _disposed;
    private long _lastProcessedLogId = -1;
    private long _lastSeenTick;
    private int _currentUrlIndex;
    private int _rpcRequestId = 1;

    public event EventHandler<string>? MessageReceived;
    public event EventHandler<Exception>? ErrorOccurred;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;

    public QubicWebSocketClient(
        List<string> webSocketUrls,
        EventProcessor eventProcessor,
        int reconnectDelaySeconds = 5,
        string? logIdFilePath = null)
    {
        if (webSocketUrls == null || webSocketUrls.Count == 0)
            throw new ArgumentException("At least one WebSocket URL is required", nameof(webSocketUrls));

        _webSocketUrls = webSocketUrls;
        _eventProcessor = eventProcessor;
        _reconnectDelaySeconds = reconnectDelaySeconds;
        _currentUrlIndex = 0;

        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        _logIdFilePath = logIdFilePath ?? Path.Combine(dataDir, "last_logid.txt");
        _tickFilePath = Path.Combine(dataDir, "last_tick.txt");

        // Load last processed logId and tick from file
        LoadLastLogId();
        LoadLastTick();
    }

    private string CurrentUrl => _webSocketUrls[_currentUrlIndex];

    private void SwitchToNextUrl()
    {
        var previousUrl = CurrentUrl;
        _currentUrlIndex = (_currentUrlIndex + 1) % _webSocketUrls.Count;
        _log.Warning("Switching from {PreviousUrl} to {NextUrl}", previousUrl, CurrentUrl);
    }

    private void LoadLastLogId()
    {
        try
        {
            if (File.Exists(_logIdFilePath))
            {
                var content = File.ReadAllText(_logIdFilePath).Trim();
                if (long.TryParse(content, out var logId) && logId >= 0)
                {
                    _lastProcessedLogId = logId;
                    _eventProcessor.SetLastProcessedLogId(logId);
                    _log.Information("Loaded last logId from file: {LastLogId}", _lastProcessedLogId);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error loading last logId");
        }
    }

    private void SaveLastLogId()
    {
        try
        {
            // only save if we have a valid logId (> 0)
            if(_lastProcessedLogId > 0)
                File.WriteAllText(_logIdFilePath, _lastProcessedLogId.ToString());
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error saving last logId");
        }
    }

    private void LoadLastTick()
    {
        try
        {
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
            _log.Error(ex, "Error loading last tick");
        }
    }

    private void SaveLastTick()
    {
        try
        {
            // Save tick - 1 so we have a buffer when checking server status
            var saveValue = _lastSeenTick > 0 ? _lastSeenTick - 1 : 0;
            File.WriteAllText(_tickFilePath, saveValue.ToString());
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error saving last tick");
        }
    }

    public void UpdateLastLogId(long logId)
    {
        if (logId > _lastProcessedLogId)
        {
            _lastProcessedLogId = logId;
            _eventProcessor.SetLastProcessedLogId(logId);
            SaveLastLogId();
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
            catch (Exception ex)
            {
                _log.Error(ex, "WebSocket error on {Url}", CurrentUrl);
                ErrorOccurred?.Invoke(this, ex);

                // Switch to next URL on connection failure
                SwitchToNextUrl();

                if (_isRunning && !_cts.Token.IsCancellationRequested)
                {
                    _log.Information("Reconnecting to {Url} in {DelaySeconds} seconds...", CurrentUrl, _reconnectDelaySeconds);
                    await Task.Delay(TimeSpan.FromSeconds(_reconnectDelaySeconds), _cts.Token);
                }
            }
        }

        _log.Information("WebSocket client stopped");
    }

    private async Task ConnectAndReceiveAsync(CancellationToken cancellationToken)
    {
        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();

        // Configure WebSocket options
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
        Connected?.Invoke(this, EventArgs.Empty);

        // Send subscription message
        await SendSubscriptionAsync(cancellationToken);

        var buffer = new byte[8192];
        var messageBuilder = new StringBuilder();

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
    }

    private async Task SendSubscriptionAsync(CancellationToken cancellationToken)
    {
        // JSON-RPC 2.0 subscription format
        // Subscribe to logType 0 (QuTransfer) and 8 (Burning)
        var subscriptionParams = new Dictionary<string, object>
        {
            ["logType"] = new[] { QubicLogTypes.QuTransfer, QubicLogTypes.Burning }
        };

        // Add startLogId if we have a previous position to resume from
        if (_lastProcessedLogId >= 0)
        {
            subscriptionParams["startLogId"] = _lastProcessedLogId;
            _log.Information("Sending JSON-RPC subscription (startLogId: {LastLogId})...", _lastProcessedLogId);
        }
        else
        {
            _log.Information("Sending JSON-RPC subscription (no startLogId - starting fresh)...");
        }

        var subscribeMessage = new
        {
            jsonrpc = "2.0",
            method = "qubic_subscribe",
            @params = new object[] { "logs", subscriptionParams },
            id = _rpcRequestId++
        };

        var json = JsonSerializer.Serialize(subscribeMessage);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _webSocket!.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            cancellationToken);

        _log.Information("JSON-RPC subscription sent successfully");
    }

    private async Task ProcessMessageAsync(string message)
    {
        try
        {
            var (logId, tick) = await _eventProcessor.ProcessMessageAsync(message);
            if (logId > 0)
            {
                UpdateLastLogId(logId);
            }
            if (tick > _lastSeenTick)
            {
                _lastSeenTick = tick;
                SaveLastTick();
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

        // Save last logId on shutdown
        SaveLastLogId();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _isRunning = false;
        SaveLastLogId();
        _cts?.Cancel();
        _cts?.Dispose();
        _webSocket?.Dispose();
    }
}
