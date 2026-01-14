namespace ContractReserveTracker.Shared.Models;

/// <summary>
/// Represents a single point in the reserve history timeline.
/// Each burn or deduct event creates one point showing the reserve at that moment.
/// </summary>
public class ReserveHistoryPoint
{
    public long Tick { get; set; }
    public DateTime Timestamp { get; set; }
    public int Epoch { get; set; }

    /// <summary>
    /// Event type: "burn" for burns (+), "deduct" for deductions (-)
    /// </summary>
    public string EventType { get; set; } = "";

    /// <summary>
    /// The amount changed: positive for burns, negative for deductions
    /// </summary>
    public long Amount { get; set; }

    /// <summary>
    /// The reserve balance after this event
    /// </summary>
    public long Reserve { get; set; }
}
