using ContractReserveTracker.Shared.Data;
using ContractReserveTracker.Shared.Models;
using ContractReserveTracker.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace ContractReserveTracker.Web.Services;

public class ReserveService
{
    private readonly IDbContextFactory<ReserveDbContext> _dbContextFactory;
    private readonly IContractInfoService _contractInfoService;

    public ReserveService(
        IDbContextFactory<ReserveDbContext> dbContextFactory,
        IContractInfoService contractInfoService)
    {
        _dbContextFactory = dbContextFactory;
        _contractInfoService = contractInfoService;
    }

    /// <summary>
    /// Get the latest epoch from all events
    /// </summary>
    public async Task<int> GetLatestEpochAsync()
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var latestBurnEpoch = await db.BurnEvents.MaxAsync(e => (int?)e.Epoch) ?? 0;
        var latestDeductEpoch = await db.DeductEvents.MaxAsync(e => (int?)e.Epoch) ?? 0;

        return Math.Max(latestBurnEpoch, latestDeductEpoch);
    }

    /// <summary>
    /// Get reserve snapshots for all contracts
    /// </summary>
    public async Task<List<ReserveSnapshot>> GetAllReserveSnapshotsAsync()
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        // Get the global latest epoch
        var latestEpoch = await GetLatestEpochAsync();

        // Get all unique contract indices from both tables
        var burnContracts = await db.BurnEvents
            .Select(e => e.ContractIndex)
            .Distinct()
            .ToListAsync();

        var deductContracts = await db.DeductEvents
            .Select(e => e.ContractIndex)
            .Distinct()
            .ToListAsync();

        var allContracts = burnContracts.Union(deductContracts).Distinct().ToList();

        var snapshots = new List<ReserveSnapshot>();

        foreach (var contractIndex in allContracts)
        {
            var snapshot = await GetReserveSnapshotAsync(contractIndex, latestEpoch);
            if (snapshot != null)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots.OrderBy(s => s.ContractIndex).ToList();
    }

    /// <summary>
    /// Get reserve snapshot for a specific contract
    /// </summary>
    /// <param name="contractIndex">The contract index</param>
    /// <param name="targetEpoch">Optional epoch for calculating epoch balance. If null, uses latest epoch from this contract's events.</param>
    public async Task<ReserveSnapshot?> GetReserveSnapshotAsync(int contractIndex, int? targetEpoch = null)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var burns = await db.BurnEvents
            .Where(e => e.ContractIndex == contractIndex)
            .ToListAsync();

        var deducts = await db.DeductEvents
            .Where(e => e.ContractIndex == contractIndex)
            .ToListAsync();

        if (!burns.Any() && !deducts.Any())
        {
            return null;
        }

        // Determine current epoch - use target epoch if provided, otherwise from this contract's events
        int currentEpoch;
        if (targetEpoch.HasValue)
        {
            currentEpoch = targetEpoch.Value;
        }
        else
        {
            var latestBurnEpoch = burns.Any() ? burns.Max(b => b.Epoch) : 0;
            var latestDeductEpoch = deducts.Any() ? deducts.Max(d => d.Epoch) : 0;
            currentEpoch = Math.Max(latestBurnEpoch, latestDeductEpoch);
        }

        // Calculate epoch-specific totals for the target epoch
        var epochBurned = burns.Where(b => b.Epoch == currentEpoch).Sum(b => b.Amount);
        var epochDeducted = deducts.Where(d => d.Epoch == currentEpoch).Sum(d => d.DeductedAmount);

        // Get last known reserve from most recent deduct event
        var lastDeduct = deducts.OrderByDescending(e => e.Tick).FirstOrDefault();
        var currentReserve = lastDeduct?.RemainingAmount ?? 0;

        // If we have burns after the last deduct, add them to the reserve
        if (lastDeduct != null)
        {
            var burnsAfterLastDeduct = burns.Where(b => b.Tick > lastDeduct.Tick).Sum(b => b.Amount);
            currentReserve += burnsAfterLastDeduct;
        }
        else
        {
            // No deducts, sum all burns
            currentReserve = burns.Sum(b => b.Amount);
        }

        var lastBurn = burns.OrderByDescending(e => e.Timestamp).FirstOrDefault();
        var lastEvent = lastDeduct?.Timestamp > lastBurn?.Timestamp ? lastDeduct?.Timestamp : lastBurn?.Timestamp;

        return new ReserveSnapshot
        {
            ContractIndex = contractIndex,
            ContractName = _contractInfoService.GetDisplayName(contractIndex),
            CurrentReserve = currentReserve,
            TotalBurned = burns.Sum(b => b.Amount),
            TotalDeducted = deducts.Sum(d => d.DeductedAmount),
            BurnCount = burns.Count,
            DeductCount = deducts.Count,
            LastEventTime = lastEvent,
            CurrentEpoch = currentEpoch,
            EpochBurned = epochBurned,
            EpochDeducted = epochDeducted
        };
    }

    /// <summary>
    /// Get recent burn events
    /// </summary>
    public async Task<List<BurnEvent>> GetRecentBurnEventsAsync(int? contractIndex = null, int limit = 50)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var query = db.BurnEvents.AsQueryable();

        if (contractIndex.HasValue)
        {
            query = query.Where(e => e.ContractIndex == contractIndex.Value);
        }

        return await query
            .OrderByDescending(e => e.Tick)
            .Take(limit)
            .ToListAsync();
    }

    /// <summary>
    /// Get recent deduct events
    /// </summary>
    public async Task<List<DeductEvent>> GetRecentDeductEventsAsync(int? contractIndex = null, int limit = 50)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var query = db.DeductEvents.AsQueryable();

        if (contractIndex.HasValue)
        {
            query = query.Where(e => e.ContractIndex == contractIndex.Value);
        }

        return await query
            .OrderByDescending(e => e.Tick)
            .Take(limit)
            .ToListAsync();
    }

    /// <summary>
    /// Get reserve history for a contract - each burn/deduct event with the running reserve total.
    /// Returns events ordered by tick (chronological order).
    /// </summary>
    public async Task<List<ReserveHistoryPoint>> GetReserveHistoryAsync(int contractIndex, int limit = 500)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var burns = await db.BurnEvents
            .Where(e => e.ContractIndex == contractIndex)
            .OrderBy(e => e.Tick)
            .ToListAsync();

        var deducts = await db.DeductEvents
            .Where(e => e.ContractIndex == contractIndex)
            .OrderBy(e => e.Tick)
            .ToListAsync();

        // Combine and sort all events by tick
        var allEvents = new List<ReserveHistoryPoint>();

        foreach (var burn in burns)
        {
            allEvents.Add(new ReserveHistoryPoint
            {
                Tick = burn.Tick,
                Timestamp = burn.Timestamp,
                Epoch = burn.Epoch,
                EventType = "burn",
                Amount = burn.Amount,
                Reserve = 0 // Will calculate below
            });
        }

        foreach (var deduct in deducts)
        {
            allEvents.Add(new ReserveHistoryPoint
            {
                Tick = deduct.Tick,
                Timestamp = deduct.Timestamp,
                Epoch = deduct.Epoch,
                EventType = "deduct",
                Amount = -deduct.DeductedAmount,
                Reserve = deduct.RemainingAmount // Deduct events have the remaining amount
            });
        }

        // Sort by tick
        allEvents = allEvents.OrderBy(e => e.Tick).ToList();

        // Calculate running reserve for burn events
        // We know the reserve at each deduct event (RemainingAmount)
        // Work backwards and forwards to fill in burn event reserves
        long runningReserve = 0;

        for (int i = 0; i < allEvents.Count; i++)
        {
            var evt = allEvents[i];
            if (evt.EventType == "deduct")
            {
                // Deduct events already have the correct reserve (remaining amount after deduction)
                runningReserve = evt.Reserve;
            }
            else
            {
                // Burn event - add to running reserve
                runningReserve += evt.Amount;
                evt.Reserve = runningReserve;
            }
        }

        // If we only have burns (no deducts to anchor), start from 0
        // The calculation above handles this correctly

        // Limit results if needed
        if (allEvents.Count > limit)
        {
            allEvents = allEvents.TakeLast(limit).ToList();
        }

        return allEvents;
    }

    /// <summary>
    /// Get events for a contract grouped by epoch for charting
    /// </summary>
    public async Task<List<(int Epoch, long TotalBurned, long TotalDeducted)>> GetEpochSummaryAsync(int contractIndex)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var burnsByEpoch = await db.BurnEvents
            .Where(e => e.ContractIndex == contractIndex)
            .GroupBy(e => e.Epoch)
            .Select(g => new { Epoch = g.Key, Total = g.Sum(e => e.Amount) })
            .ToDictionaryAsync(x => x.Epoch, x => x.Total);

        var deductsByEpoch = await db.DeductEvents
            .Where(e => e.ContractIndex == contractIndex)
            .GroupBy(e => e.Epoch)
            .Select(g => new { Epoch = g.Key, Total = g.Sum(e => e.DeductedAmount) })
            .ToDictionaryAsync(x => x.Epoch, x => x.Total);

        var allEpochs = burnsByEpoch.Keys.Union(deductsByEpoch.Keys).OrderBy(e => e);

        return allEpochs.Select(epoch => (
            Epoch: epoch,
            TotalBurned: burnsByEpoch.GetValueOrDefault(epoch, 0),
            TotalDeducted: deductsByEpoch.GetValueOrDefault(epoch, 0)
        )).ToList();
    }
}
