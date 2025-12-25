using System.Globalization;
using System.Text.Json;
using ContractReserveTracker.Collector.Models;
using ContractReserveTracker.Shared.Data;
using ContractReserveTracker.Shared.Models;
using ContractReserveTracker.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace ContractReserveTracker.Collector.Services;

public class EventProcessor
{
    private readonly ILogger _log = Log.ForContext<EventProcessor>();
    private readonly IDbContextFactory<ReserveDbContext> _dbContextFactory;
    private readonly IContractInfoService _contractInfoService;
    private readonly SignalRPublisher? _signalRPublisher;

    public EventProcessor(
        IDbContextFactory<ReserveDbContext> dbContextFactory,
        IContractInfoService contractInfoService,
        SignalRPublisher? signalRPublisher = null)
    {
        _dbContextFactory = dbContextFactory;
        _contractInfoService = contractInfoService;
        _signalRPublisher = signalRPublisher;
    }

    public async Task<(long logId, long tick, int epoch)> ProcessMessageAsync(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeElement) ||
                typeElement.GetString() != "log")
            {
                return (0, 0, 0);
            }

            if (!root.TryGetProperty("logType", out var logTypeElement))
            {
                return (0, 0, 0);
            }

            var logType = logTypeElement.GetInt32();
            var isCatchUp = root.TryGetProperty("isCatchUp", out var catchUpElement) && catchUpElement.GetBoolean();

            if (!root.TryGetProperty("message", out var messageElement))
            {
                return (0, 0, 0);
            }

            var logId = messageElement.TryGetProperty("logId", out var logIdElement) ? logIdElement.GetInt64() : 0;
            var tick = messageElement.TryGetProperty("tick", out var tickElement) ? tickElement.GetInt64() : 0;
            var epoch = messageElement.TryGetProperty("epoch", out var epochElement) ? epochElement.GetInt32() : 0;

            if (logType == QubicLogTypes.Burning)
            {
                await ProcessBurnEventAsync(messageElement, isCatchUp);
            }
            else if (logType == QubicLogTypes.ContractReserveDeduction)
            {
                await ProcessDeductEventAsync(messageElement, isCatchUp);
            }

            return (logId, tick, epoch);
        }
        catch (JsonException ex)
        {
            _log.Error(ex, "Error parsing JSON message");
            return (0, 0, 0);
        }
    }

    private async Task ProcessBurnEventAsync(JsonElement messageElement, bool isCatchUp)
    {
        if (!messageElement.TryGetProperty("body", out var bodyElement))
        {
            return;
        }

        var burnEvent = new BurnEvent
        {
            ContractIndex = bodyElement.TryGetProperty("contractIndexBurnedFor", out var ciElement) ? ciElement.GetInt32() : 0,
            Amount = bodyElement.TryGetProperty("amount", out var amountElement) ? amountElement.GetInt64() : 0,
            PublicKey = bodyElement.TryGetProperty("publicKey", out var pkElement) ? pkElement.GetString() ?? "" : "",
            Epoch = messageElement.TryGetProperty("epoch", out var epochElement) ? epochElement.GetInt32() : 0,
            Tick = messageElement.TryGetProperty("tick", out var tickElement) ? tickElement.GetInt64() : 0,
            TxHash = messageElement.TryGetProperty("txHash", out var txElement) ? txElement.GetString() ?? "" : "",
            LogId = messageElement.TryGetProperty("logId", out var logIdElement) ? logIdElement.GetInt64() : 0,
            Timestamp = ParseTimestamp(messageElement)
        };

        var contractName = _contractInfoService.GetDisplayName(burnEvent.ContractIndex);

        if (!isCatchUp)
        {
            _log.Information("BURN: {ContractName} +{Amount} QU from {PublicKey}",
                contractName,
                FormatAmount(burnEvent.Amount),
                TruncateAddress(burnEvent.PublicKey));
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();

        // Check if already exists (by LogId)
        if (await db.BurnEvents.AnyAsync(e => e.LogId == burnEvent.LogId))
        {
            return;
        }

        db.BurnEvents.Add(burnEvent);
        await db.SaveChangesAsync();

        if (_signalRPublisher != null && !isCatchUp)
        {
            await _signalRPublisher.PublishBurnEventAsync(burnEvent);
        }
    }

    private async Task ProcessDeductEventAsync(JsonElement messageElement, bool isCatchUp)
    {
        if (!messageElement.TryGetProperty("body", out var bodyElement))
        {
            return;
        }

        var deductEvent = new DeductEvent
        {
            ContractIndex = bodyElement.TryGetProperty("contractIndex", out var ciElement) ? ciElement.GetInt32() : 0,
            DeductedAmount = bodyElement.TryGetProperty("deductedAmount", out var daElement) ? daElement.GetInt64() : 0,
            RemainingAmount = bodyElement.TryGetProperty("remainingAmount", out var raElement) ? raElement.GetInt64() : 0,
            Epoch = messageElement.TryGetProperty("epoch", out var epochElement) ? epochElement.GetInt32() : 0,
            Tick = messageElement.TryGetProperty("tick", out var tickElement) ? tickElement.GetInt64() : 0,
            TxHash = messageElement.TryGetProperty("txHash", out var txElement) ? txElement.GetString() ?? "" : "",
            LogId = messageElement.TryGetProperty("logId", out var logIdElement) ? logIdElement.GetInt64() : 0,
            Timestamp = ParseTimestamp(messageElement)
        };

        var contractName = _contractInfoService.GetDisplayName(deductEvent.ContractIndex);

        if (!isCatchUp)
        {
            _log.Information("DEDUCT: {ContractName} -{Amount} QU (remaining: {Remaining} QU)",
                contractName,
                FormatAmount(deductEvent.DeductedAmount),
                FormatAmount(deductEvent.RemainingAmount));
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();

        // Check if already exists (by LogId)
        if (await db.DeductEvents.AnyAsync(e => e.LogId == deductEvent.LogId))
        {
            return;
        }

        db.DeductEvents.Add(deductEvent);
        await db.SaveChangesAsync();

        if (_signalRPublisher != null && !isCatchUp)
        {
            await _signalRPublisher.PublishDeductEventAsync(deductEvent);
        }
    }

    private DateTime ParseTimestamp(JsonElement messageElement)
    {
        if (messageElement.TryGetProperty("timestamp", out var tsElement))
        {
            var tsString = tsElement.GetString();
            if (!string.IsNullOrEmpty(tsString))
            {
                // Format: "25-12-25 19:12:44" -> "yy-MM-dd HH:mm:ss"
                if (DateTime.TryParseExact(tsString, "yy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                }
            }
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
