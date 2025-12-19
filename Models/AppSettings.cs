namespace QubicTransferWatcher.Models;

public class AppSettings
{
    /// <summary>
    /// List of WebSocket URLs to try in order. Falls back to next URL on connection failure or tick delay.
    /// </summary>
    public List<string> WebSocketUrls { get; set; } = new();

    /// <summary>
    /// Maximum allowed tick delay before switching to next WebSocket URL.
    /// If server's currentVerifiedTick is more than this many ticks behind the latest known tick, switch servers.
    /// </summary>
    public int MaxTickDelay { get; set; } = 10;

    public string DiscordWebhookUrl { get; set; } = "";
    public string BundleJsonUrl { get; set; } = "https://static.qubic.org/v1/general/data/bundle.json";
    public long MinTransferAmount { get; set; } = 2_000_000_000;
    public int ReconnectDelaySeconds { get; set; } = 5;
    public string SeqUrl { get; set; } = "http://localhost:5341";
    public string? SeqApiKey { get; set; }
    public string PriceApiUrl { get; set; } = "https://api.coingecko.com/api/v3/simple/price?ids=qubic-network&vs_currencies=usd";
    public string ExplorerAddressUrl { get; set; } = "https://explorer.qubic.org/network/address/";
    public string ExplorerTickUrl { get; set; } = "https://explorer.qubic.org/network/tick/";
}
