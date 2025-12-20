using Microsoft.Extensions.Configuration;
using QubicTransferWatcher.Models;
using QubicTransferWatcher.Services;
using Serilog;
using Serilog.Events;

namespace QubicTransferWatcher;

class Program
{
    static async Task Main(string[] args)
    {
        // Load configuration using .NET Configuration
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
            .Enrich.WithProperty("Application", "QubicTransferWatcher")
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");

        // Add Seq sink if configured
        if (!string.IsNullOrEmpty(settings.SeqUrl))
        {
            if (!string.IsNullOrEmpty(settings.SeqApiKey))
            {
                logConfig.WriteTo.Seq(settings.SeqUrl, apiKey: settings.SeqApiKey);
            }
            else
            {
                logConfig.WriteTo.Seq(settings.SeqUrl);
            }
        }

        Log.Logger = logConfig.CreateLogger();

        try
        {
            Log.Information("========================================");
            Log.Information("   Qubic Transfer Watcher");
            Log.Information("========================================");

            // Validate Discord webhook URL
            if (string.IsNullOrEmpty(settings.DiscordWebhookUrl))
            {
                Log.Warning("Discord webhook URL not configured! Set DISCORD_WEBHOOK_URL environment variable or update appsettings.json");
            }

            // Create HTTP client with SSL bypass for self-signed certs
            var httpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            var httpClient = new HttpClient(httpHandler);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "QubicTransferWatcher/1.0");

            // Initialize services
            Log.Information("Initializing services...");

            var addressLabelService = new AddressLabelService(httpClient, settings.BundleJsonUrl);
            await addressLabelService.InitializeAsync();

            var priceService = new PriceService(httpClient, settings.PriceApiUrl);
            await priceService.InitializeAsync();

            var discordService = new DiscordService(
                httpClient,
                settings.DiscordWebhookUrl,
                addressLabelService,
                priceService,
                settings.ExplorerAddressUrl,
                settings.ExplorerTickUrl,
                settings.ExplorerTxUrl);

            var eventProcessor = new EventProcessor(
                addressLabelService,
                discordService,
                settings.MinTransferAmount);

            var webSocketClient = new QubicWebSocketClient(
                settings.WebSocketUrls,
                eventProcessor,
                settings.ReconnectDelaySeconds,
                settings.MaxTickDelay);

            // Setup graceful shutdown
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += async (sender, e) =>
            {
                e.Cancel = true;
                Log.Information("Shutting down...");
                await discordService.SendShutdownMessageAsync();
                cts.Cancel();
            };

            AppDomain.CurrentDomain.ProcessExit += async (sender, e) =>
            {
                await discordService.SendShutdownMessageAsync();
                cts.Cancel();
            };

            // Send startup notification
            await discordService.SendStartupMessageAsync();

            // Print configuration
            Log.Information("Configuration:");
            Log.Information("  WebSocket URLs: {WebSocketUrls}", string.Join(", ", settings.WebSocketUrls));
            Log.Information("  Max Tick Delay: {MaxTickDelay}", settings.MaxTickDelay);
            Log.Information("  Min Transfer Amount: {MinAmount} QUBIC", PriceService.FormatQubicAmount(settings.MinTransferAmount));
            Log.Information("  Discord Webhook: {DiscordStatus}", string.IsNullOrEmpty(settings.DiscordWebhookUrl) ? "Not configured" : "Configured");
            Log.Information("  Seq URL: {SeqUrl}", settings.SeqUrl);
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
