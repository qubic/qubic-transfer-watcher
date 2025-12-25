namespace ContractReserveTracker.Web.Services.Collector;

/// <summary>
/// Qubic WebSocket log type constants
/// </summary>
public static class QubicLogTypes
{
    /// <summary>
    /// BURNING - Token burn event (increases contract reserve)
    /// </summary>
    public const int Burning = 8;

    /// <summary>
    /// CONTRACT_RESERVE_DEDUCTION - Reserve deduction event (decreases contract reserve)
    /// </summary>
    public const int ContractReserveDeduction = 13;
}
