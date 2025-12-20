using System.Text.Json;
using QubicTransferWatcher.Models;
using Serilog;

namespace QubicTransferWatcher.Services;

/// <summary>
/// Processes incoming WebSocket messages and filters events for notification
/// </summary>
public class EventProcessor
{
    private readonly ILogger _log = Log.ForContext<EventProcessor>();
    private readonly AddressLabelService _addressLabelService;
    private readonly DiscordService _discordService;
    private readonly long _minTransferAmount;
    private long _lastProcessedLogId = -1;

    public EventProcessor(
        AddressLabelService addressLabelService,
        DiscordService discordService,
        long minTransferAmount)
    {
        _addressLabelService = addressLabelService;
        _discordService = discordService;
        _minTransferAmount = minTransferAmount;
    }

    /// <summary>
    /// Update the last processed logId (called by WebSocketClient when logId is persisted)
    /// </summary>
    public void SetLastProcessedLogId(long logId)
    {
        _lastProcessedLogId = logId;
    }

    /// <summary>
    /// Process a WebSocket message and return the logId and tick (for tracking)
    /// </summary>
    /// <returns>Tuple of (logId, tick) from the message, or (0, 0) if not found</returns>
    public async Task<(long logId, long tick)> ProcessMessageAsync(string message)
    {
        long logId = 0;
        long tick = 0;

        try
        {
            var (transfer, messageLogId, messageTick) = ParseMessage(message);
            logId = messageLogId;
            tick = messageTick;

            if (transfer == null)
            {
                return (logId, tick);
            }

            // Ignore events we've already processed
            if (logId > 0 && logId <= _lastProcessedLogId)
            {
                _log.Debug("Ignoring already processed event logId {LogId} (last: {LastLogId})",
                    logId, _lastProcessedLogId);
                return (logId, tick);
            }

            // Apply filtering rules
            if (ShouldNotify(transfer))
            {
                _log.Information("Event matched: {EventType} - {Amount} QUBIC (tick: {Tick})",
                    transfer.IsBurn ? "BURN" : "TRANSFER",
                    PriceService.FormatQubicAmount(transfer.Amount),
                    transfer.Tick);
                await _discordService.SendTransferNotificationAsync(transfer);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error processing message");
        }

        return (logId, tick);
    }

    private (TransferEvent? transfer, long logId, long tick) ParseMessage(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            long logId = 0;
            long tick = 0;

            // Check if this is a "log" type message from the Qubic WebSocket
            if (!root.TryGetProperty("type", out var typeElement) ||
                typeElement.GetString() != "log")
            {
                return (null, logId, tick);
            }

            // Extract logId from root
            if (root.TryGetProperty("logId", out var logIdElement))
            {
                logId = logIdElement.GetInt64();
            }

            // Get the logType to determine if this is a transfer or burn
            if (!root.TryGetProperty("logType", out var logTypeElement))
            {
                return (null, logId, tick);
            }

            var logType = logTypeElement.GetInt32();

            // Get the message object
            if (!root.TryGetProperty("message", out var messageElement))
            {
                return (null, logId, tick);
            }

            // Extract tick
            if (messageElement.TryGetProperty("tick", out var tickElement))
            {
                tick = tickElement.GetInt64();
            }

            // Extract timestamp (format: "25-12-18 07:20:52")
            DateTime? timestamp = null;
            if (messageElement.TryGetProperty("timestamp", out var timestampElement))
            {
                var timestampStr = timestampElement.GetString();
                if (!string.IsNullOrEmpty(timestampStr) &&
                    DateTime.TryParseExact(timestampStr, "yy-MM-dd HH:mm:ss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedTimestamp))
                {
                    timestamp = DateTime.SpecifyKind(parsedTimestamp, DateTimeKind.Utc);
                }
            }

            // Get body
            if (!messageElement.TryGetProperty("body", out var bodyElement))
            {
                return (null, logId, tick);
            }

            // Get transaction ID - prefer txHash, fallback to logDigest
            string txHash = "";
            if (messageElement.TryGetProperty("txHash", out var txHashElement))
            {
                txHash = txHashElement.GetString() ?? "";
            }
            else if (messageElement.TryGetProperty("logDigest", out var digestElement))
            {
                txHash = digestElement.GetString() ?? "";
            }

            TransferEvent? transfer = null;

            if (logType == QubicLogTypes.QuTransfer)
            {
                // QU_TRANSFER: { "from": "...", "to": "...", "amount": ... }
                transfer = ParseTransferBody(bodyElement, txHash, tick, timestamp);
            }
            else if (logType == QubicLogTypes.Burning)
            {
                // BURNING: { "publicKey": "...", "amount": ..., "contractIndexBurnedFor": ... }
                transfer = ParseBurnBody(bodyElement, txHash, tick, timestamp);
            }

            return (transfer, logId, tick);
        }
        catch (JsonException ex)
        {
            _log.Debug("Failed to parse message as JSON: {Error}", ex.Message);
            return (null, 0, 0);
        }
    }

    private TransferEvent? ParseTransferBody(JsonElement body, string txHash, long tick, DateTime? timestamp)
    {
        var transfer = new TransferEvent
        {
            TxHash = txHash,
            Tick = tick,
            IsBurn = false,
            Timestamp = timestamp ?? DateTime.UtcNow
        };

        // Get from address
        if (body.TryGetProperty("from", out var fromElement))
        {
            transfer.FromAddress = fromElement.GetString() ?? "";
        }

        // Get to address
        if (body.TryGetProperty("to", out var toElement))
        {
            transfer.ToAddress = toElement.GetString() ?? "";
        }

        // Get amount
        if (body.TryGetProperty("amount", out var amountElement))
        {
            transfer.Amount = amountElement.GetInt64();
        }

        // Validate required fields
        if (string.IsNullOrEmpty(transfer.FromAddress) || transfer.Amount <= 0)
        {
            return null;
        }

        // Check if destination is burn address
        transfer.IsBurn = _addressLabelService.IsBurnAddress(transfer.ToAddress);

        return transfer;
    }

    private TransferEvent? ParseBurnBody(JsonElement body, string txHash, long tick, DateTime? timestamp)
    {
        var transfer = new TransferEvent
        {
            TxHash = txHash,
            Tick = tick,
            IsBurn = true,
            ToAddress = AddressLabelService.BurnAddress,
            Timestamp = timestamp ?? DateTime.UtcNow
        };

        // Get publicKey (the address that burned)
        if (body.TryGetProperty("publicKey", out var publicKeyElement))
        {
            transfer.FromAddress = publicKeyElement.GetString() ?? "";
        }

        // Get amount
        if (body.TryGetProperty("amount", out var amountElement))
        {
            transfer.Amount = amountElement.GetInt64();
        }

        // Validate required fields
        if (string.IsNullOrEmpty(transfer.FromAddress) || transfer.Amount <= 0)
        {
            return null;
        }

        return transfer;
    }

    private bool ShouldNotify(TransferEvent transfer)
    {
        // Ignore 1M transfers to Qutil burn address (fee payments)
        if (transfer.ToAddress == AddressLabelService.BurnAddressQutil && transfer.Amount == 1_000_000)
        {
            return false;
        }

        // Always notify for burn events
        if (transfer.IsBurn)
        {
            return true;
        }

        // For regular transfers, only notify if amount exceeds threshold
        return transfer.Amount >= _minTransferAmount;
    }
}
