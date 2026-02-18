namespace QubicTransferWatcher.Models;

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
