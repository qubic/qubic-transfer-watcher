using ContractReserveTracker.Shared.Hubs;
using ContractReserveTracker.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace ContractReserveTracker.Web.Hubs;

/// <summary>
/// SignalR hub for real-time reserve updates
/// </summary>
public class ReserveHub : Hub<IReserveHubClient>
{
    private readonly ILogger<ReserveHub> _logger;

    public ReserveHub(ILogger<ReserveHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Called by collector to broadcast burn event to all clients
    /// </summary>
    public async Task OnBurnEvent(BurnEvent burnEvent)
    {
        await Clients.All.OnBurnEvent(burnEvent);
    }

    /// <summary>
    /// Called by collector to broadcast deduct event to all clients
    /// </summary>
    public async Task OnDeductEvent(DeductEvent deductEvent)
    {
        await Clients.All.OnDeductEvent(deductEvent);
    }

    /// <summary>
    /// Called by collector to broadcast reserve update to all clients
    /// </summary>
    public async Task OnReserveUpdate(ReserveSnapshot snapshot)
    {
        await Clients.All.OnReserveUpdate(snapshot);
    }
}
