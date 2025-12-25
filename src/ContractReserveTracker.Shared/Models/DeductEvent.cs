using System.ComponentModel.DataAnnotations;

namespace ContractReserveTracker.Shared.Models;

/// <summary>
/// Represents a reserve deduction event (type 13) - decreases contract reserve
/// </summary>
public class DeductEvent
{
    [Key]
    public long Id { get; set; }

    /// <summary>
    /// The contract index that was deducted from
    /// </summary>
    public int ContractIndex { get; set; }

    /// <summary>
    /// Amount deducted (in QU)
    /// </summary>
    public long DeductedAmount { get; set; }

    /// <summary>
    /// Remaining reserve amount after deduction (in QU)
    /// </summary>
    public long RemainingAmount { get; set; }

    /// <summary>
    /// Epoch when the deduction occurred
    /// </summary>
    public int Epoch { get; set; }

    /// <summary>
    /// Tick when the deduction occurred
    /// </summary>
    public long Tick { get; set; }

    /// <summary>
    /// Transaction hash
    /// </summary>
    public string TxHash { get; set; } = "";

    /// <summary>
    /// Log ID from the WebSocket stream
    /// </summary>
    public long LogId { get; set; }

    /// <summary>
    /// Timestamp of the event
    /// </summary>
    public DateTime Timestamp { get; set; }
}
