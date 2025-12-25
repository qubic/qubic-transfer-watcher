using System.ComponentModel.DataAnnotations;

namespace ContractReserveTracker.Shared.Models;

/// <summary>
/// Represents a burn event (type 8) - increases contract reserve
/// </summary>
public class BurnEvent
{
    [Key]
    public long Id { get; set; }

    /// <summary>
    /// The contract index that received the burn (contractIndexBurnedFor)
    /// </summary>
    public int ContractIndex { get; set; }

    /// <summary>
    /// Amount burned (in QU)
    /// </summary>
    public long Amount { get; set; }

    /// <summary>
    /// Public key of the address that burned
    /// </summary>
    public string PublicKey { get; set; } = "";

    /// <summary>
    /// Epoch when the burn occurred
    /// </summary>
    public int Epoch { get; set; }

    /// <summary>
    /// Tick when the burn occurred
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
