using ContractReserveTracker.Shared.Data;
using ContractReserveTracker.Shared.Hubs;
using ContractReserveTracker.Shared.Services;
using ContractReserveTracker.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Qubic.Bob;
using Serilog;

namespace ContractReserveTracker.Web.Services.Collector;

public class CollectorHostedService : BackgroundService
{
    private readonly Serilog.ILogger _log = Log.ForContext<CollectorHostedService>();
    private readonly IConfiguration _configuration;
    private readonly IDbContextFactory<ReserveDbContext> _dbContextFactory;
    private readonly IContractInfoService _contractInfoService;
    private readonly IHubContext<ReserveHub, IReserveHubClient> _hubContext;
    private QubicLogCollector? _collector;
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
        var bobNodes = _configuration.GetSection("Collector:BobNodes").Get<List<string>>();
        if (bobNodes == null || bobNodes.Count == 0)
        {
            _log.Warning("No Bob nodes configured. Collector will not run.");
            return;
        }

        var reconnectDelaySeconds = _configuration.GetValue("Collector:ReconnectDelaySeconds", 5);
        var epochsToKeep = _configuration.GetValue("Collector:EpochsToKeep", 3);

        _log.Information("========================================");
        _log.Information("   Contract Reserve Tracker - Collector");
        _log.Information("========================================");
        _log.Information("Configuration:");
        _log.Information("  Bob Nodes: {BobNodes}", string.Join(", ", bobNodes));
        _log.Information("  Epochs to keep: {EpochsToKeep}", epochsToKeep);

        // Initialize data cleanup service
        _dataCleanupService = new DataCleanupService(_dbContextFactory, epochsToKeep);

        // Initialize event processor with hub context for real-time updates
        var eventProcessor = new EventProcessor(_dbContextFactory, _contractInfoService, _hubContext);

        var bobOptions = new BobWebSocketOptions
        {
            Nodes = bobNodes.ToArray(),
            ReconnectDelay = TimeSpan.FromSeconds(reconnectDelaySeconds),
            OnConnectionEvent = evt =>
            {
                if (evt.Exception != null)
                    _log.Warning(evt.Exception, "Bob: {Message}", evt.Message);
                else
                    _log.Information("Bob: [{EventType}] {Message}", evt.Type, evt.Message);
            }
        };

        _collector = new QubicLogCollector(bobOptions, eventProcessor, _dbContextFactory);

        try
        {
            await _collector.RunAsync(stoppingToken);
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
            await _collector.DisposeAsync();
        }

        _log.Information("Collector stopped");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.Information("Stopping collector...");
        if (_collector != null)
        {
            await _collector.DisposeAsync();
        }
        await base.StopAsync(cancellationToken);
    }
}
