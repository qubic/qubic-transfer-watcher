namespace ContractReserveTracker.Collector.Models;

public class AppSettings
{
    /// <summary>
    /// List of WebSocket URLs to try in order. Falls back to next URL on connection failure.
    /// </summary>
    public List<string> WebSocketUrls { get; set; } = new();

    /// <summary>
    /// Maximum allowed tick delay before switching to next WebSocket URL.
    /// </summary>
    public int MaxTickDelay { get; set; } = 10;

    /// <summary>
    /// Path to SQLite database file
    /// </summary>
    public string DatabasePath { get; set; } = "data/reserves.db";

    /// <summary>
    /// URL of the SignalR hub for real-time updates
    /// </summary>
    public string SignalRHubUrl { get; set; } = "http://localhost:5000/reserveHub";

    /// <summary>
    /// URL to fetch smart contract information
    /// </summary>
    public string SmartContractsUrl { get; set; } = "https://static.qubic.org/v1/general/data/smart_contracts.json";

    /// <summary>
    /// How often to refresh contract info (in minutes)
    /// </summary>
    public int ContractInfoRefreshMinutes { get; set; } = 60;

    /// <summary>
    /// Number of epochs to keep in database
    /// </summary>
    public int EpochsToKeep { get; set; } = 3;

    /// <summary>
    /// Delay in seconds before reconnecting after disconnect
    /// </summary>
    public int ReconnectDelaySeconds { get; set; } = 5;
}
