using System.Text.Json;
using Qubic.Bob.Models;
using QubicTransferWatcher.Models;
using Serilog;

namespace QubicTransferWatcher.Services;

/// <summary>
/// Processes log notifications and filters events for Discord notification.
/// </summary>
public class EventProcessor
{
    private readonly ILogger _log = Log.ForContext<EventProcessor>();
    private readonly AddressLabelService _addressLabelService;
    private readonly DiscordService _discordService;
    private readonly long _minTransferAmount;

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
    /// Process a typed log notification from BobWebSocketClient.
    /// </summary>
    public async Task ProcessNotificationAsync(LogNotification notification)
    {
        try
        {
            if (notification.Body is null)
                return;

            var timestamp = ParseTimestamp(notification.Timestamp);
            var txHash = notification.TxHash ?? notification.LogDigest ?? "";

            TransferEvent? transfer = null;

            if (notification.LogType == QubicLogTypes.QuTransfer)
            {
                transfer = ParseTransferBody(notification.Body.Value, txHash, notification.Tick, timestamp);
            }
            else if (notification.LogType == QubicLogTypes.Burning)
            {
                transfer = ParseBurnBody(notification.Body.Value, txHash, notification.Tick, timestamp);
            }

            if (transfer == null)
                return;

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
            _log.Error(ex, "Error processing notification");
        }
    }

    private static DateTime ParseTimestamp(JsonElement? timestampElement)
    {
        if (!timestampElement.HasValue)
            return DateTime.UtcNow;

        if (timestampElement.Value.ValueKind == JsonValueKind.String)
        {
            var timestampStr = timestampElement.Value.GetString();
            if (!string.IsNullOrEmpty(timestampStr) &&
                DateTime.TryParseExact(timestampStr, "yy-MM-dd HH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedTimestamp))
            {
                return DateTime.SpecifyKind(parsedTimestamp, DateTimeKind.Utc);
            }
        }
        else if (timestampElement.Value.ValueKind == JsonValueKind.Number)
        {
            var unixSeconds = timestampElement.Value.GetInt64();
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        }

        return DateTime.UtcNow;
    }

    private TransferEvent? ParseTransferBody(JsonElement body, string txHash, uint tick, DateTime timestamp)
    {
        var transfer = new TransferEvent
        {
            TxHash = txHash,
            Tick = tick,
            IsBurn = false,
            Timestamp = timestamp
        };

        if (body.TryGetProperty("from", out var fromElement))
            transfer.FromAddress = fromElement.GetString() ?? "";

        if (body.TryGetProperty("to", out var toElement))
            transfer.ToAddress = toElement.GetString() ?? "";

        if (body.TryGetProperty("amount", out var amountElement))
            transfer.Amount = amountElement.GetInt64();

        if (string.IsNullOrEmpty(transfer.FromAddress) || transfer.Amount <= 0)
            return null;

        transfer.IsBurn = _addressLabelService.IsBurnAddress(transfer.ToAddress);
        return transfer;
    }

    private TransferEvent? ParseBurnBody(JsonElement body, string txHash, uint tick, DateTime timestamp)
    {
        var transfer = new TransferEvent
        {
            TxHash = txHash,
            Tick = tick,
            IsBurn = true,
            ToAddress = AddressLabelService.BurnAddress,
            Timestamp = timestamp
        };

        if (body.TryGetProperty("publicKey", out var publicKeyElement))
            transfer.FromAddress = publicKeyElement.GetString() ?? "";

        if (body.TryGetProperty("amount", out var amountElement))
            transfer.Amount = amountElement.GetInt64();

        if (string.IsNullOrEmpty(transfer.FromAddress) || transfer.Amount <= 0)
            return null;

        return transfer;
    }

    private bool ShouldNotify(TransferEvent transfer)
    {
        // Ignore 1M transfers to Qutil burn address (fee payments)
        if (transfer.ToAddress == AddressLabelService.BurnAddressQutil && transfer.Amount == 1_000_000)
            return false;

        // Always notify for burn events
        if (transfer.IsBurn)
            return true;

        // For regular transfers, only notify if amount exceeds threshold
        return transfer.Amount >= _minTransferAmount;
    }
}
