using System.Globalization;
using System.Text.Json;
using ContractReserveTracker.Shared.Data;
using ContractReserveTracker.Shared.Hubs;
using ContractReserveTracker.Shared.Models;
using ContractReserveTracker.Shared.Services;
using ContractReserveTracker.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Qubic.Bob.Models;
using Serilog;

namespace ContractReserveTracker.Web.Services.Collector;

public class EventProcessor
{
    private readonly Serilog.ILogger _log = Log.ForContext<EventProcessor>();
    private readonly IDbContextFactory<ReserveDbContext> _dbContextFactory;
    private readonly IContractInfoService _contractInfoService;
    private readonly IHubContext<ReserveHub, IReserveHubClient>? _hubContext;

    public EventProcessor(
        IDbContextFactory<ReserveDbContext> dbContextFactory,
        IContractInfoService contractInfoService,
        IHubContext<ReserveHub, IReserveHubClient>? hubContext = null)
    {
        _dbContextFactory = dbContextFactory;
        _contractInfoService = contractInfoService;
        _hubContext = hubContext;
    }

    /// <summary>
    /// Process a typed log notification from BobWebSocketClient.
    /// </summary>
    public async Task ProcessNotificationAsync(LogNotification notification)
    {
        try
        {
            if (notification.Body is null)
                return;

            if (notification.LogType == QubicLogTypes.Burning)
            {
                await ProcessBurnEventAsync(notification);
            }
            else if (notification.LogType == QubicLogTypes.ContractReserveDeduction)
            {
                await ProcessDeductEventAsync(notification);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error processing notification (logId: {LogId})", notification.LogId);
        }
    }

    private async Task ProcessBurnEventAsync(LogNotification notification)
    {
        var body = notification.Body!.Value;

        long amount = ParseLongValue(body, "amount");

        int contractIndex = 0;
        if (body.TryGetProperty("contractIndexBurnedFor", out var ciElement))
            contractIndex = ciElement.GetInt32();

        var publicKey = body.TryGetProperty("publicKey", out var pkElement) ? pkElement.GetString() ?? "" : "";

        var burnEvent = new BurnEvent
        {
            ContractIndex = contractIndex,
            Amount = amount,
            PublicKey = publicKey,
            Epoch = (int)notification.Epoch,
            Tick = notification.Tick,
            TxHash = notification.TxHash ?? "",
            LogId = notification.LogId,
            Timestamp = ParseTimestamp(notification.Timestamp)
        };

        var contractName = _contractInfoService.GetDisplayName(burnEvent.ContractIndex);

        if (!notification.IsCatchUp)
        {
            _log.Information("BURN: {ContractName} +{Amount} QU from {PublicKey}",
                contractName,
                FormatAmount(burnEvent.Amount),
                TruncateAddress(burnEvent.PublicKey));
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();

        if (await db.BurnEvents.AnyAsync(e => e.Epoch == burnEvent.Epoch && e.LogId == burnEvent.LogId))
            return;

        db.BurnEvents.Add(burnEvent);
        await db.SaveChangesAsync();

        if (_hubContext != null && !notification.IsCatchUp)
        {
            await _hubContext.Clients.All.OnBurnEvent(burnEvent);
        }
    }

    private async Task ProcessDeductEventAsync(LogNotification notification)
    {
        var body = notification.Body!.Value;

        long deductedAmount = ParseLongValue(body, "deductedAmount");
        long remainingAmount = ParseLongValue(body, "remainingAmount");

        int contractIndex = 0;
        if (body.TryGetProperty("contractIndex", out var ciElement))
            contractIndex = ciElement.GetInt32();

        var deductEvent = new DeductEvent
        {
            ContractIndex = contractIndex,
            DeductedAmount = deductedAmount,
            RemainingAmount = remainingAmount,
            Epoch = (int)notification.Epoch,
            Tick = notification.Tick,
            TxHash = notification.TxHash ?? "",
            LogId = notification.LogId,
            Timestamp = ParseTimestamp(notification.Timestamp)
        };

        var contractName = _contractInfoService.GetDisplayName(deductEvent.ContractIndex);

        if (!notification.IsCatchUp)
        {
            _log.Information("DEDUCT: {ContractName} -{Amount} QU (remaining: {Remaining} QU)",
                contractName,
                FormatAmount(deductEvent.DeductedAmount),
                FormatAmount(deductEvent.RemainingAmount));
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();

        if (await db.DeductEvents.AnyAsync(e => e.Epoch == deductEvent.Epoch && e.LogId == deductEvent.LogId))
            return;

        db.DeductEvents.Add(deductEvent);
        await db.SaveChangesAsync();

        if (_hubContext != null && !notification.IsCatchUp)
        {
            await _hubContext.Clients.All.OnDeductEvent(deductEvent);
        }
    }

    private static long ParseLongValue(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return 0;

        if (prop.ValueKind == JsonValueKind.String)
            return long.TryParse(prop.GetString(), out var val) ? val : 0;

        if (prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt64();

        return 0;
    }

    private static DateTime ParseTimestamp(JsonElement? timestampElement)
    {
        if (!timestampElement.HasValue)
            return DateTime.UtcNow;

        if (timestampElement.Value.ValueKind == JsonValueKind.String)
        {
            var tsString = timestampElement.Value.GetString();
            if (!string.IsNullOrEmpty(tsString))
            {
                // Try ISO 8601 format first
                if (DateTime.TryParse(tsString, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                {
                    return parsed;
                }
                // Fall back to old format: "yy-MM-dd HH:mm:ss"
                if (DateTime.TryParseExact(tsString, "yy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
                {
                    return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                }
            }
        }
        else if (timestampElement.Value.ValueKind == JsonValueKind.Number)
        {
            var unixSeconds = timestampElement.Value.GetInt64();
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        }

        return DateTime.UtcNow;
    }

    private static string FormatAmount(long amount)
    {
        return amount.ToString("N0");
    }

    private static string TruncateAddress(string address)
    {
        if (string.IsNullOrEmpty(address) || address.Length <= 12)
            return address;
        return $"{address[..6]}...{address[^6..]}";
    }
}
