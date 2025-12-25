namespace ContractReserveTracker.Shared.Models;

/// <summary>
/// Calculated reserve state for a contract at a point in time
/// </summary>
public class ReserveSnapshot
{
    /// <summary>
    /// Contract index
    /// </summary>
    public int ContractIndex { get; set; }

    /// <summary>
    /// Contract display name
    /// </summary>
    public string ContractName { get; set; } = "";

    /// <summary>
    /// Current reserve amount (from last deduct event's remainingAmount)
    /// </summary>
    public long CurrentReserve { get; set; }

    /// <summary>
    /// Total amount burned for this contract
    /// </summary>
    public long TotalBurned { get; set; }

    /// <summary>
    /// Total amount deducted from this contract
    /// </summary>
    public long TotalDeducted { get; set; }

    /// <summary>
    /// Number of burn events
    /// </summary>
    public int BurnCount { get; set; }

    /// <summary>
    /// Number of deduct events
    /// </summary>
    public int DeductCount { get; set; }

    /// <summary>
    /// Timestamp of last event
    /// </summary>
    public DateTime? LastEventTime { get; set; }

    /// <summary>
    /// Current epoch number
    /// </summary>
    public int CurrentEpoch { get; set; }

    /// <summary>
    /// Amount burned in current epoch
    /// </summary>
    public long EpochBurned { get; set; }

    /// <summary>
    /// Amount deducted in current epoch
    /// </summary>
    public long EpochDeducted { get; set; }

    /// <summary>
    /// Net balance for current epoch (burned - deducted)
    /// </summary>
    public long EpochBalance => EpochBurned - EpochDeducted;
}
