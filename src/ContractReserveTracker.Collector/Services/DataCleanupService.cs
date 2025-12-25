using ContractReserveTracker.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace ContractReserveTracker.Collector.Services;

public class DataCleanupService
{
    private readonly ILogger _log = Log.ForContext<DataCleanupService>();
    private readonly IDbContextFactory<ReserveDbContext> _dbContextFactory;
    private readonly int _epochsToKeep;
    private readonly Timer _cleanupTimer;

    public DataCleanupService(IDbContextFactory<ReserveDbContext> dbContextFactory, int epochsToKeep = 3)
    {
        _dbContextFactory = dbContextFactory;
        _epochsToKeep = epochsToKeep;

        // Run cleanup every hour
        _cleanupTimer = new Timer(
            async _ => await CleanupOldDataAsync(),
            null,
            TimeSpan.FromMinutes(5), // First run after 5 minutes
            TimeSpan.FromHours(1));
    }

    public async Task CleanupOldDataAsync()
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            // Get current epoch from the most recent event
            var latestBurnEpoch = await db.BurnEvents
                .OrderByDescending(e => e.Epoch)
                .Select(e => e.Epoch)
                .FirstOrDefaultAsync();

            var latestDeductEpoch = await db.DeductEvents
                .OrderByDescending(e => e.Epoch)
                .Select(e => e.Epoch)
                .FirstOrDefaultAsync();

            var currentEpoch = Math.Max(latestBurnEpoch, latestDeductEpoch);
            if (currentEpoch == 0)
            {
                return; // No data yet
            }

            var cutoffEpoch = currentEpoch - _epochsToKeep;

            // Delete old burn events
            var deletedBurns = await db.BurnEvents
                .Where(e => e.Epoch < cutoffEpoch)
                .ExecuteDeleteAsync();

            // Delete old deduct events
            var deletedDeducts = await db.DeductEvents
                .Where(e => e.Epoch < cutoffEpoch)
                .ExecuteDeleteAsync();

            if (deletedBurns > 0 || deletedDeducts > 0)
            {
                _log.Information("Cleanup: Deleted {Burns} burn events and {Deducts} deduct events older than epoch {Cutoff}",
                    deletedBurns, deletedDeducts, cutoffEpoch);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error during data cleanup");
        }
    }
}
