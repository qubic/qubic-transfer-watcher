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
    /// Get reserve snapshots for all contracts
    /// </summary>
    public async Task<List<ReserveSnapshot>> GetAllReserveSnapshotsAsync()
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

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
            var snapshot = await GetReserveSnapshotAsync(contractIndex);
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
    public async Task<ReserveSnapshot?> GetReserveSnapshotAsync(int contractIndex)
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
            LastEventTime = lastEvent
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
