using System.ComponentModel.DataAnnotations;

namespace ContractReserveTracker.Shared.Models;

/// <summary>
/// Tracks the last processed log ID per epoch
/// </summary>
public class LogProgress
{
    [Key]
    public int Epoch { get; set; }

    /// <summary>
    /// Last processed log ID for this epoch
    /// </summary>
    public long LastLogId { get; set; }

    /// <summary>
    /// Last seen tick for this epoch
    /// </summary>
    public long LastTick { get; set; }

    /// <summary>
    /// When this record was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}
