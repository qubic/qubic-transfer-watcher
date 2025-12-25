using ContractReserveTracker.Shared.Data;
using ContractReserveTracker.Shared.Services;
using ContractReserveTracker.Web.Components;
using ContractReserveTracker.Web.Hubs;
using ContractReserveTracker.Web.Services;
using ContractReserveTracker.Web.Services.Collector;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;

// Configure Serilog early
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentName()
    .Enrich.WithProperty("Application", "ContractReserveTracker.Web")
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Use Serilog
    builder.Host.UseSerilog();

    // Configuration
    var databasePath = builder.Configuration.GetValue<string>("DatabasePath") ?? "data/reserves.db";
    var smartContractsUrl = builder.Configuration.GetValue<string>("SmartContractsUrl")
        ?? "https://static.qubic.org/v1/general/data/smart_contracts.json";
    var contractInfoRefreshMinutes = builder.Configuration.GetValue<int>("ContractInfoRefreshMinutes", 60);

    // Ensure data directory exists
    var dbPath = Path.GetFullPath(databasePath);
    var dbDir = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrEmpty(dbDir))
    {
        Directory.CreateDirectory(dbDir);
    }

    // Database
    builder.Services.AddDbContextFactory<ReserveDbContext>(options =>
        options.UseSqlite($"Data Source={dbPath}"));

    // HTTP client
    builder.Services.AddHttpClient();

    // Contract info service
    builder.Services.AddSingleton<IContractInfoService>(sp =>
    {
        var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "ContractReserveTracker.Web/1.0");
        var logger = sp.GetRequiredService<ILogger<ContractInfoService>>();
        return new ContractInfoService(httpClient, logger, smartContractsUrl, contractInfoRefreshMinutes);
    });

    // Reserve service
    builder.Services.AddScoped<ReserveService>();

    // SignalR
    builder.Services.AddSignalR();

    // Collector background service
    builder.Services.AddHostedService<CollectorHostedService>();

    // Razor components
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    var app = builder.Build();

    // Ensure database is created
    using (var scope = app.Services.CreateScope())
    {
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ReserveDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        Log.Information("Database initialized at {Path}", dbPath);
    }

    // Initialize contract info service
    var contractInfoService = app.Services.GetRequiredService<IContractInfoService>();
    await contractInfoService.InitializeAsync();

    // Configure the HTTP request pipeline.
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
    }

    app.UseStaticFiles();
    app.UseAntiforgery();

    // Map SignalR hub
    app.MapHub<ReserveHub>("/reserveHub");

    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    Log.Information("Starting web application...");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
