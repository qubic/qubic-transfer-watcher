namespace QubicTransferWatcher.Models;

public class AppSettings
{
    /// <summary>
    /// Bob node base URLs (e.g., "https://bob.qubic.li"). BobWebSocketClient derives HTTP and WS endpoints from these.
    /// </summary>
    public List<string> BobNodes { get; set; } = new();

    public string DiscordWebhookUrl { get; set; } = "";
    public string BundleJsonUrl { get; set; } = "https://static.qubic.org/v1/general/data/bundle.json";
    public long MinTransferAmount { get; set; } = 2_000_000_000;
    public long MinBurnAmount { get; set; } = 50;
    public int ReconnectDelaySeconds { get; set; } = 5;
    public string SeqUrl { get; set; } = "http://localhost:5341";
    public string? SeqApiKey { get; set; }
    public string PriceApiUrl { get; set; } = "https://api.coingecko.com/api/v3/simple/price?ids=qubic-network&vs_currencies=usd";
    public string ExplorerAddressUrl { get; set; } = "https://explorer.qubic.org/network/address/";
    public string ExplorerTickUrl { get; set; } = "https://explorer.qubic.org/network/tick/";
    public string ExplorerTxUrl { get; set; } = "https://explorer.qubic.org/network/tx/";
}
