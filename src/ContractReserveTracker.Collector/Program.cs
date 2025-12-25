using ContractReserveTracker.Collector.Models;
using ContractReserveTracker.Collector.Services;
using ContractReserveTracker.Shared.Data;
using ContractReserveTracker.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace ContractReserveTracker.Collector;

class Program
{
    static async Task Main(string[] args)
    {
        // Load configuration
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        var settings = configuration.Get<AppSettings>() ?? new AppSettings();

        // Configure Serilog
        var logConfig = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#endif
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithEnvironmentName()
            .Enrich.WithProperty("Application", "ContractReserveTracker.Collector")
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");

        Log.Logger = logConfig.CreateLogger();

        try
        {
            Log.Information("========================================");
            Log.Information("   Contract Reserve Tracker - Collector");
            Log.Information("========================================");

            // Ensure data directory exists
            var dbPath = Path.GetFullPath(settings.DatabasePath);
            var dbDir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dbDir))
            {
                Directory.CreateDirectory(dbDir);
            }

            // Setup DI
            var services = new ServiceCollection();

            // Database
            services.AddDbContextFactory<ReserveDbContext>(options =>
                options.UseSqlite($"Data Source={dbPath}"));

            // Logging for DI
            services.AddLogging(builder => builder.AddSerilog(Log.Logger));

            // HTTP client for contract info service
            services.AddHttpClient();

            var serviceProvider = services.BuildServiceProvider();

            // Ensure database is created
            await using (var db = await serviceProvider.GetRequiredService<IDbContextFactory<ReserveDbContext>>().CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                Log.Information("Database initialized at {Path}", dbPath);
            }

            // Initialize contract info service
            var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "ContractReserveTracker/1.0");

            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var contractInfoService = new ContractInfoService(
                httpClient,
                loggerFactory.CreateLogger<ContractInfoService>(),
                settings.SmartContractsUrl,
                settings.ContractInfoRefreshMinutes);
            await contractInfoService.InitializeAsync();

            // Initialize SignalR publisher (optional - may fail if web app not running)
            SignalRPublisher? signalRPublisher = null;
            try
            {
                signalRPublisher = new SignalRPublisher(settings.SignalRHubUrl);
                await signalRPublisher.StartAsync();
            }
            catch (Exception ex)
            {
                Log.Warning("Could not connect to SignalR hub: {Error}. Running without real-time updates.", ex.Message);
            }

            // Initialize services
            var dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<ReserveDbContext>>();
            var eventProcessor = new EventProcessor(dbContextFactory, contractInfoService, signalRPublisher);
            var dataCleanupService = new DataCleanupService(dbContextFactory, settings.EpochsToKeep);

            var webSocketClient = new QubicWebSocketClient(
                settings.WebSocketUrls,
                eventProcessor,
                dbContextFactory,
                settings.ReconnectDelaySeconds,
                settings.MaxTickDelay);

            // Setup graceful shutdown
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Log.Information("Shutting down...");
                cts.Cancel();
            };

            AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
            {
                cts.Cancel();
            };

            // Print configuration
            Log.Information("Configuration:");
            Log.Information("  WebSocket URLs: {WebSocketUrls}", string.Join(", ", settings.WebSocketUrls));
            Log.Information("  Database: {DatabasePath}", dbPath);
            Log.Information("  SignalR Hub: {SignalRHubUrl}", settings.SignalRHubUrl);
            Log.Information("  Epochs to keep: {EpochsToKeep}", settings.EpochsToKeep);
            Log.Information("Press Ctrl+C to stop...");

            // Start monitoring
            try
            {
                await webSocketClient.StartAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            finally
            {
                webSocketClient.Dispose();
                if (signalRPublisher != null)
                {
                    await signalRPublisher.DisposeAsync();
                }
            }

            Log.Information("Goodbye!");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
