using ContractReserveTracker.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace ContractReserveTracker.Shared.Data;

public class ReserveDbContext : DbContext
{
    public ReserveDbContext(DbContextOptions<ReserveDbContext> options) : base(options)
    {
    }

    public DbSet<BurnEvent> BurnEvents => Set<BurnEvent>();
    public DbSet<DeductEvent> DeductEvents => Set<DeductEvent>();
    public DbSet<LogProgress> LogProgress => Set<LogProgress>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // BurnEvent indexes
        modelBuilder.Entity<BurnEvent>(entity =>
        {
            // LogId is only unique within an epoch
            entity.HasIndex(e => new { e.Epoch, e.LogId }).IsUnique();
            entity.HasIndex(e => e.ContractIndex);
            entity.HasIndex(e => e.Tick);
            entity.HasIndex(e => e.Timestamp);
        });

        // DeductEvent indexes
        modelBuilder.Entity<DeductEvent>(entity =>
        {
            // LogId is only unique within an epoch
            entity.HasIndex(e => new { e.Epoch, e.LogId }).IsUnique();
            entity.HasIndex(e => e.ContractIndex);
            entity.HasIndex(e => e.Tick);
            entity.HasIndex(e => e.Timestamp);
        });
    }
}
