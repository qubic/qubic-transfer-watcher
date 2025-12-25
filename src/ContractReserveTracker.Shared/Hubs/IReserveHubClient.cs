using ContractReserveTracker.Shared.Models;

namespace ContractReserveTracker.Shared.Hubs;

/// <summary>
/// SignalR hub client interface for real-time reserve updates
/// </summary>
public interface IReserveHubClient
{
    /// <summary>
    /// Called when a new burn event is received
    /// </summary>
    Task OnBurnEvent(BurnEvent burnEvent);

    /// <summary>
    /// Called when a new deduct event is received
    /// </summary>
    Task OnDeductEvent(DeductEvent deductEvent);

    /// <summary>
    /// Called when reserve snapshot is updated for a contract
    /// </summary>
    Task OnReserveUpdate(ReserveSnapshot snapshot);
}
