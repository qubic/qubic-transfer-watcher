using System.Text;
using System.Text.Json;
using QubicTransferWatcher.Models;
using Serilog;

namespace QubicTransferWatcher.Services;

/// <summary>
/// Service to send notifications to Discord via webhook
/// </summary>
public class DiscordService
{
    private readonly ILogger _log = Log.ForContext<DiscordService>();
    private readonly HttpClient _httpClient;
    private readonly string _webhookUrl;
    private readonly AddressLabelService _addressLabelService;
    private readonly PriceService _priceService;
    private readonly string _explorerAddressUrl;
    private readonly string _explorerTickUrl;

    public DiscordService(
        HttpClient httpClient,
        string webhookUrl,
        AddressLabelService addressLabelService,
        PriceService priceService,
        string explorerAddressUrl,
        string explorerTickUrl)
    {
        _httpClient = httpClient;
        _webhookUrl = webhookUrl;
        _addressLabelService = addressLabelService;
        _priceService = priceService;
        _explorerAddressUrl = explorerAddressUrl;
        _explorerTickUrl = explorerTickUrl;
    }

    public async Task SendTransferNotificationAsync(TransferEvent transfer)
    {
        if (string.IsNullOrEmpty(_webhookUrl))
        {
            _log.Warning("Discord webhook URL not configured, skipping notification");
            return;
        }

        try
        {
            var message = await BuildMessageAsync(transfer);
            await SendWebhookMessageAsync(message);

            var eventType = transfer.IsBurn ? "BURN" : "TRANSFER";
            _log.Information("Discord notification sent for {EventType}: {Amount} QUBIC",
                eventType, PriceService.FormatQubicAmount(transfer.Amount));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error sending Discord notification");
        }
    }


    private async Task<string> BuildMessageAsync(TransferEvent transfer)
    {
        var usdValue = await _priceService.CalculateUsdValue(transfer.Amount);
        var fromLabel = _addressLabelService.GetLabel(transfer.FromAddress);
        var toLabel = _addressLabelService.GetLabel(transfer.ToAddress);

        // Format addresses with explorer links
        var fromLink = FormatAddressLink(transfer.FromAddress, fromLabel);
        var toLink = FormatAddressLink(transfer.ToAddress, toLabel);

        // Determine emoji based on amount and type
        var emoji = GetEmoji(transfer.Amount, transfer.IsBurn);

        // Format the message similar to the example
        var qubicFormatted = PriceService.FormatQubicAmount(transfer.Amount);
        var usdFormatted = PriceService.FormatNumber(usdValue);
        var tickLink = $"[{transfer.Tick}](<{_explorerTickUrl}{transfer.Tick}>)";

        string content;
        if (transfer.IsBurn)
        {
            content = $"{emoji} **{qubicFormatted} QUBIC** ({usdFormatted} USD) burned from {fromLink}\nTick: {tickLink}";
        }
        else
        {
            content = $"{emoji} **{qubicFormatted} QUBIC** ({usdFormatted} USD) transferred from {fromLink} to {toLink}\nTick: {tickLink}";
        }

        return content;
    }

    private string FormatAddressLink(string address, string label)
    {
        if (string.IsNullOrEmpty(address))
            return "Unknown";

        // Use angle brackets to suppress Discord link embeds
        return $"[{label}](<{_explorerAddressUrl}{address}>)";
    }

    private static string GetEmoji(long amount, bool isBurn)
    {
        if (isBurn)
        {
            // More fire emojis for larger burns
            if (amount >= 100_000_000_000) return "🔥🔥🔥🔥🔥🔥";
            if (amount >= 50_000_000_000) return "🔥🔥🔥🔥🔥";
            if (amount >= 20_000_000_000) return "🔥🔥🔥🔥";
            if (amount >= 10_000_000_000) return "🔥🔥🔥";
            if (amount >= 5_000_000_000) return "🔥🔥";
            return "🔥";
        }
        else
        {
            // Whale emoji for large transfers
            if (amount >= 100_000_000_000) return "🐋🐋🐋🐋🐋";
            if (amount >= 50_000_000_000) return "🐋🐋🐋🐋";
            if (amount >= 20_000_000_000) return "🐋🐋🐋";
            if (amount >= 10_000_000_000) return "🐋🐋";
            if (amount >= 5_000_000_000) return "🐋";
            return "💸";
        }
    }

    private async Task SendWebhookMessageAsync(string content)
    {
        var payload = new
        {
            content,
            username = "Qubic Whale Alert"
        };

        var json = JsonSerializer.Serialize(payload);
        var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(_webhookUrl, httpContent);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Discord webhook failed: {response.StatusCode} - {error}");
        }

        // Respect Discord rate limits
        await Task.Delay(500);
    }

    public async Task SendStartupMessageAsync()
    {
        if (string.IsNullOrEmpty(_webhookUrl))
            return;

        try
        {
            await SendWebhookMessageAsync("🟢 **Qubic Transfer Watcher** started and monitoring transactions...");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error sending startup message");
        }
    }

    public async Task SendShutdownMessageAsync()
    {
        if (string.IsNullOrEmpty(_webhookUrl))
            return;

        try
        {
            await SendWebhookMessageAsync("🔴 **Qubic Transfer Watcher** stopped.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error sending shutdown message");
        }
    }
}
