using ContractReserveTracker.Shared.Data;
using ContractReserveTracker.Shared.Hubs;
using ContractReserveTracker.Shared.Services;
using ContractReserveTracker.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace ContractReserveTracker.Web.Services.Collector;

public class CollectorHostedService : BackgroundService
{
    private readonly Serilog.ILogger _log = Log.ForContext<CollectorHostedService>();
    private readonly IConfiguration _configuration;
    private readonly IDbContextFactory<ReserveDbContext> _dbContextFactory;
    private readonly IContractInfoService _contractInfoService;
    private readonly IHubContext<ReserveHub, IReserveHubClient> _hubContext;
    private QubicWebSocketClient? _webSocketClient;
    private DataCleanupService? _dataCleanupService;

    public CollectorHostedService(
        IConfiguration configuration,
        IDbContextFactory<ReserveDbContext> dbContextFactory,
        IContractInfoService contractInfoService,
        IHubContext<ReserveHub, IReserveHubClient> hubContext)
    {
        _configuration = configuration;
        _dbContextFactory = dbContextFactory;
        _contractInfoService = contractInfoService;
        _hubContext = hubContext;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var webSocketUrls = _configuration.GetSection("Collector:WebSocketUrls").Get<List<string>>();
        if (webSocketUrls == null || webSocketUrls.Count == 0)
        {
            _log.Warning("No WebSocket URLs configured. Collector will not run.");
            return;
        }

        var reconnectDelaySeconds = _configuration.GetValue("Collector:ReconnectDelaySeconds", 5);
        var epochsToKeep = _configuration.GetValue("Collector:EpochsToKeep", 3);

        _log.Information("========================================");
        _log.Information("   Contract Reserve Tracker - Collector");
        _log.Information("========================================");
        _log.Information("Configuration:");
        _log.Information("  WebSocket URLs: {WebSocketUrls}", string.Join(", ", webSocketUrls));
        _log.Information("  Epochs to keep: {EpochsToKeep}", epochsToKeep);

        // Initialize data cleanup service
        _dataCleanupService = new DataCleanupService(_dbContextFactory, epochsToKeep);

        // Initialize event processor with hub context for real-time updates
        var eventProcessor = new EventProcessor(_dbContextFactory, _contractInfoService, _hubContext);

        // Initialize WebSocket client
        _webSocketClient = new QubicWebSocketClient(
            webSocketUrls,
            eventProcessor,
            _dbContextFactory,
            reconnectDelaySeconds);

        try
        {
            await _webSocketClient.StartAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Collector encountered an error");
        }
        finally
        {
            _webSocketClient.Dispose();
        }

        _log.Information("Collector stopped");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.Information("Stopping collector...");
        if (_webSocketClient != null)
        {
            await _webSocketClient.StopAsync();
        }
        await base.StopAsync(cancellationToken);
    }
}
