using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Data;

/// <summary>
/// Read-only context for queries that may be served by a read replica
/// (<c>ConnectionStrings:ReadReplica</c>; it falls back to the primary when none is configured).
/// Tracking is off and saving is blocked, so it can never write by accident.
/// </summary>
public class ReadDbContext : ApplicationDbContext
{
    public ReadDbContext(DbContextOptions<ReadDbContext> options)
        : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new InvalidOperationException("The read-only context cannot save changes.");

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The read-only context cannot save changes.");
}
