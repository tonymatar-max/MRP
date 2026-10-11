using CobaltMrp.Domain;
using Microsoft.EntityFrameworkCore;

namespace CobaltMrp.Data;

/// <summary>Persists and retrieves saved MRP run snapshots.</summary>
public sealed class RunRepository
{
    private readonly CobaltMrpDbContext _db;

    public RunRepository(CobaltMrpDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>Save a run together with all its children in one transaction.</summary>
    public async Task SaveRunAsync(MrpRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        _db.Runs.Add(run);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Load one run with all children, or null if not found.</summary>
    public Task<MrpRun?> GetRunAsync(Guid id, CancellationToken ct = default)
        => _db.Runs
            .Include(r => r.PlannedOrders)
            .Include(r => r.Pegging)
            .Include(r => r.Exceptions)
            .Include(r => r.CapacityLoads)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == id, ct);

    /// <summary>List run headers (no children), latest first.</summary>
    public async Task<IReadOnlyList<MrpRun>> ListRunsAsync(int take = 50, CancellationToken ct = default)
    {
        // DateTimeOffset ordering is not translatable on some providers; SQL Server handles it.
        return await _db.Runs.AsNoTracking()
            .OrderByDescending(r => r.RunAt)
            .Take(take)
            .ToListAsync(ct);
    }

    /// <summary>Persist changes to firmed/pushed state of planned orders already tracked by the context.</summary>
    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
