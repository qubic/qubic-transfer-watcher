using System.Text.Json.Serialization;

namespace QubicTransferWatcher.Models;

/// <summary>
/// Represents a WebSocket log message from the Qubic network
/// </summary>
public class WebSocketLogMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("data")]
    public LogData? Data { get; set; }
}

public class LogData
{
    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = "";

    [JsonPropertyName("sourceId")]
    public string SourceId { get; set; } = "";

    [JsonPropertyName("destId")]
    public string DestId { get; set; } = "";

    [JsonPropertyName("amount")]
    public long Amount { get; set; }

    [JsonPropertyName("txId")]
    public string TxId { get; set; } = "";

    [JsonPropertyName("tick")]
    public long Tick { get; set; }

    // Alternative property names that might be used
    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("to")]
    public string? To { get; set; }

    [JsonPropertyName("value")]
    public long? Value { get; set; }

    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("transactionHash")]
    public string? TransactionHash { get; set; }

    // Helper properties to normalize different message formats
    public string GetSourceAddress() => !string.IsNullOrEmpty(SourceId) ? SourceId : From ?? "";
    public string GetDestAddress() => !string.IsNullOrEmpty(DestId) ? DestId : To ?? "";
    public long GetAmount() => Amount > 0 ? Amount : Value ?? 0;
    public string GetTxHash() => !string.IsNullOrEmpty(TxId) ? TxId : TransactionHash ?? Hash ?? "";
}

/// <summary>
/// Processed transfer event ready for notification
/// </summary>
public class TransferEvent
{
    public string FromAddress { get; set; } = "";
    public string ToAddress { get; set; } = "";
    public long Amount { get; set; }
    public string TxHash { get; set; } = "";
    public long Tick { get; set; }
    public bool IsBurn { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
