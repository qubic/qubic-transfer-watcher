using ContractReserveTracker.Shared.Hubs;
using ContractReserveTracker.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Serilog;

namespace ContractReserveTracker.Collector.Services;

public class SignalRPublisher : IAsyncDisposable
{
    private readonly ILogger _log = Log.ForContext<SignalRPublisher>();
    private readonly HubConnection _hubConnection;
    private bool _isConnected;

    public SignalRPublisher(string hubUrl)
    {
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.Closed += async (error) =>
        {
            _isConnected = false;
            _log.Warning("SignalR connection closed: {Error}", error?.Message);
            await Task.Delay(5000);
            await TryConnectAsync();
        };

        _hubConnection.Reconnected += (connectionId) =>
        {
            _isConnected = true;
            _log.Information("SignalR reconnected with connection ID: {ConnectionId}", connectionId);
            return Task.CompletedTask;
        };
    }

    public async Task StartAsync()
    {
        await TryConnectAsync();
    }

    private async Task TryConnectAsync()
    {
        try
        {
            await _hubConnection.StartAsync();
            _isConnected = true;
            _log.Information("Connected to SignalR hub");
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _log.Warning("Failed to connect to SignalR hub: {Error}. Will retry...", ex.Message);
        }
    }

    public async Task PublishBurnEventAsync(BurnEvent burnEvent)
    {
        if (!_isConnected) return;

        try
        {
            await _hubConnection.InvokeAsync(nameof(IReserveHubClient.OnBurnEvent), burnEvent);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error publishing burn event to SignalR");
        }
    }

    public async Task PublishDeductEventAsync(DeductEvent deductEvent)
    {
        if (!_isConnected) return;

        try
        {
            await _hubConnection.InvokeAsync(nameof(IReserveHubClient.OnDeductEvent), deductEvent);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error publishing deduct event to SignalR");
        }
    }

    public async Task PublishReserveUpdateAsync(ReserveSnapshot snapshot)
    {
        if (!_isConnected) return;

        try
        {
            await _hubConnection.InvokeAsync(nameof(IReserveHubClient.OnReserveUpdate), snapshot);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error publishing reserve update to SignalR");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _hubConnection.DisposeAsync();
    }
}
