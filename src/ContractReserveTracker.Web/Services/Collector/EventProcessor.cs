using System.Globalization;
using System.Text.Json;
using ContractReserveTracker.Shared.Data;
using ContractReserveTracker.Shared.Hubs;
using ContractReserveTracker.Shared.Models;
using ContractReserveTracker.Shared.Services;
using ContractReserveTracker.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
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
    /// Process incoming WebSocket message. Supports both old format and new JSON-RPC 2.0 format.
    ///
    /// New format (JSON-RPC notification):
    /// {
    ///   "jsonrpc": "2.0",
    ///   "method": "qubic_subscription",
    ///   "params": {
    ///     "subscription": "qubic_sub_0",
    ///     "result": [{ tick, epoch, logId, type, body, ... }, ...]
    ///   }
    /// }
    /// </summary>
    public async Task<(long logId, long tick, int epoch)> ProcessMessageAsync(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            // Check for JSON-RPC 2.0 notification format
            if (root.TryGetProperty("jsonrpc", out _) &&
                root.TryGetProperty("method", out var methodElement) &&
                methodElement.GetString() == "qubic_subscription")
            {
                return await ProcessJsonRpcNotificationAsync(root);
            }

            // Legacy format support (type: "log")
            if (root.TryGetProperty("type", out var typeElement) &&
                typeElement.GetString() == "log")
            {
                return await ProcessLegacyLogMessageAsync(root);
            }

            return (0, 0, 0);
        }
        catch (JsonException ex)
        {
            _log.Error(ex, "Error parsing JSON message");
            return (0, 0, 0);
        }
    }

    private async Task<(long logId, long tick, int epoch)> ProcessJsonRpcNotificationAsync(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var paramsElement))
        {
            return (0, 0, 0);
        }

        if (!paramsElement.TryGetProperty("result", out var resultElement))
        {
            return (0, 0, 0);
        }

        long lastLogId = 0;
        long lastTick = 0;
        int lastEpoch = 0;

        // Result can be a single object or an array of objects
        if (resultElement.ValueKind == JsonValueKind.Array)
        {
            // Process each log entry in the result array
            foreach (var logEntry in resultElement.EnumerateArray())
            {
                var (logId, tick, epoch) = await ProcessLogEntryAsync(logEntry);
                if (logId > lastLogId) lastLogId = logId;
                if (tick > lastTick) lastTick = tick;
                if (epoch > lastEpoch) lastEpoch = epoch;
            }
        }
        else if (resultElement.ValueKind == JsonValueKind.Object)
        {
            // Single log entry object
            (lastLogId, lastTick, lastEpoch) = await ProcessLogEntryAsync(resultElement);
        }

        return (lastLogId, lastTick, lastEpoch);
    }

    private async Task<(long logId, long tick, int epoch)> ProcessLogEntryAsync(JsonElement logEntry)
    {
        var logId = logEntry.TryGetProperty("logId", out var logIdEl) ? logIdEl.GetInt64() : 0;
        var tick = logEntry.TryGetProperty("tick", out var tickEl) ? tickEl.GetInt64() : 0;
        var epoch = logEntry.TryGetProperty("epoch", out var epochEl) ? epochEl.GetInt32() : 0;
        // Field is "type" in the RPC response (not "logType")
        var logType = logEntry.TryGetProperty("type", out var typeEl) ? typeEl.GetInt32() : -1;

        // isCatchUp may be present at log level
        var isCatchUp = logEntry.TryGetProperty("isCatchUp", out var catchUpEl) && catchUpEl.GetBoolean();

        if (logType == QubicLogTypes.Burning)
        {
            await ProcessBurnEventFromJsonRpcAsync(logEntry, isCatchUp);
        }
        else if (logType == QubicLogTypes.ContractReserveDeduction)
        {
            await ProcessDeductEventFromJsonRpcAsync(logEntry, isCatchUp);
        }

        return (logId, tick, epoch);
    }

    private async Task<(long logId, long tick, int epoch)> ProcessLegacyLogMessageAsync(JsonElement root)
    {
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

    /// <summary>
    /// Process burn event from new JSON-RPC format.
    /// Data is inside the "body" object: { contractIndexBurnedFor, amount, publicKey }
    /// </summary>
    private async Task ProcessBurnEventFromJsonRpcAsync(JsonElement logEntry, bool isCatchUp)
    {
        if (!logEntry.TryGetProperty("body", out var body))
        {
            _log.Warning("Burn event missing body element");
            return;
        }

        // Parse amount - can be number or string
        long amount = ParseLongValue(body, "amount");

        // Contract index is in "contractIndexBurnedFor"
        int contractIndex = 0;
        if (body.TryGetProperty("contractIndexBurnedFor", out var ciElement))
        {
            contractIndex = ciElement.GetInt32();
        }

        // Public key is in body.publicKey
        var publicKey = body.TryGetProperty("publicKey", out var pkElement) ? pkElement.GetString() ?? "" : "";

        // Transaction hash is at root level as "txHash"
        var txHash = logEntry.TryGetProperty("txHash", out var txElement) ? txElement.GetString() ?? "" : "";

        var burnEvent = new BurnEvent
        {
            ContractIndex = contractIndex,
            Amount = amount,
            PublicKey = publicKey,
            Epoch = logEntry.TryGetProperty("epoch", out var epochElement) ? epochElement.GetInt32() : 0,
            Tick = logEntry.TryGetProperty("tick", out var tickElement) ? tickElement.GetInt64() : 0,
            TxHash = txHash,
            LogId = logEntry.TryGetProperty("logId", out var logIdElement) ? logIdElement.GetInt64() : 0,
            Timestamp = ParseTimestampFromJsonRpc(logEntry)
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

        if (await db.BurnEvents.AnyAsync(e => e.Epoch == burnEvent.Epoch && e.LogId == burnEvent.LogId))
        {
            return;
        }

        db.BurnEvents.Add(burnEvent);
        await db.SaveChangesAsync();

        if (_hubContext != null && !isCatchUp)
        {
            await _hubContext.Clients.All.OnBurnEvent(burnEvent);
        }
    }

    /// <summary>
    /// Process deduct event from new JSON-RPC format where fields are at the log entry level.
    /// Fields can be at root level or inside a "body" object depending on Bob version.
    /// </summary>
    private async Task ProcessDeductEventFromJsonRpcAsync(JsonElement logEntry, bool isCatchUp)
    {
        // Try to get body element, but fall back to logEntry itself if not present
        var dataElement = logEntry.TryGetProperty("body", out var bodyElement) ? bodyElement : logEntry;

        // Parse amounts - can be string or number
        long deductedAmount = ParseLongValue(dataElement, "deductedAmount");
        long remainingAmount = ParseLongValue(dataElement, "remainingAmount");

        // Contract index
        int contractIndex = 0;
        if (dataElement.TryGetProperty("contractIndex", out var ciElement))
        {
            contractIndex = ciElement.GetInt32();
        }

        // Transaction hash can be "txHash" or "transactionHash"
        var txHash = "";
        if (logEntry.TryGetProperty("txHash", out var txElement))
        {
            txHash = txElement.GetString() ?? "";
        }
        else if (logEntry.TryGetProperty("transactionHash", out var thElement))
        {
            txHash = thElement.GetString() ?? "";
        }

        var deductEvent = new DeductEvent
        {
            ContractIndex = contractIndex,
            DeductedAmount = deductedAmount,
            RemainingAmount = remainingAmount,
            Epoch = logEntry.TryGetProperty("epoch", out var epochElement) ? epochElement.GetInt32() : 0,
            Tick = logEntry.TryGetProperty("tick", out var tickElement) ? tickElement.GetInt64() : 0,
            TxHash = txHash,
            LogId = logEntry.TryGetProperty("logId", out var logIdElement) ? logIdElement.GetInt64() : 0,
            Timestamp = ParseTimestampFromJsonRpc(logEntry)
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

        if (await db.DeductEvents.AnyAsync(e => e.Epoch == deductEvent.Epoch && e.LogId == deductEvent.LogId))
        {
            return;
        }

        db.DeductEvents.Add(deductEvent);
        await db.SaveChangesAsync();

        if (_hubContext != null && !isCatchUp)
        {
            await _hubContext.Clients.All.OnDeductEvent(deductEvent);
        }
    }

    /// <summary>
    /// Parse a long value from JSON that could be a string or number
    /// </summary>
    private static long ParseLongValue(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return 0;
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            return long.TryParse(prop.GetString(), out var val) ? val : 0;
        }

        if (prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetInt64();
        }

        return 0;
    }

    private DateTime ParseTimestampFromJsonRpc(JsonElement logEntry)
    {
        if (logEntry.TryGetProperty("timestamp", out var tsElement))
        {
            var tsString = tsElement.GetString();
            if (!string.IsNullOrEmpty(tsString))
            {
                // Try ISO 8601 format first (new format)
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
        return DateTime.UtcNow;
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

        // Check if already exists (by Epoch + LogId, since LogId is only unique within an epoch)
        if (await db.BurnEvents.AnyAsync(e => e.Epoch == burnEvent.Epoch && e.LogId == burnEvent.LogId))
        {
            return;
        }

        db.BurnEvents.Add(burnEvent);
        await db.SaveChangesAsync();

        if (_hubContext != null && !isCatchUp)
        {
            await _hubContext.Clients.All.OnBurnEvent(burnEvent);
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

        // Check if already exists (by Epoch + LogId, since LogId is only unique within an epoch)
        if (await db.DeductEvents.AnyAsync(e => e.Epoch == deductEvent.Epoch && e.LogId == deductEvent.LogId))
        {
            return;
        }

        db.DeductEvents.Add(deductEvent);
        await db.SaveChangesAsync();

        if (_hubContext != null && !isCatchUp)
        {
            await _hubContext.Clients.All.OnDeductEvent(deductEvent);
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
