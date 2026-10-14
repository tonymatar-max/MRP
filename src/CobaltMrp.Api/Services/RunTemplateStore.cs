using CobaltMrp.Data;
using CobaltMrp.Domain;
using Microsoft.EntityFrameworkCore;

namespace CobaltMrp.Api.Services;

/// <summary>Where saved Run MRP templates live.</summary>
public interface IRunTemplateStore
{
    Task<IReadOnlyList<RunTemplate>> ListAsync(CancellationToken ct);
    Task<RunTemplate?> GetAsync(Guid id, CancellationToken ct);
    Task SaveAsync(RunTemplate t, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}

/// <summary>Used while no Cobalt database is configured. Lost on restart.</summary>
public sealed class MemoryRunTemplateStore : IRunTemplateStore
{
    private readonly Dictionary<Guid, RunTemplate> _items = new();
    private readonly object _lock = new();
    private static RunTemplate Copy(RunTemplate t) => new() { Id = t.Id, Name = t.Name, CriteriaJson = t.CriteriaJson, CreatedBy = t.CreatedBy, CreatedAt = t.CreatedAt, UpdatedAt = t.UpdatedAt };

    public Task<IReadOnlyList<RunTemplate>> ListAsync(CancellationToken ct)
    { lock (_lock) return Task.FromResult<IReadOnlyList<RunTemplate>>(_items.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(Copy).ToList()); }
    public Task<RunTemplate?> GetAsync(Guid id, CancellationToken ct)
    { lock (_lock) return Task.FromResult(_items.TryGetValue(id, out var t) ? Copy(t) : null); }
    public Task SaveAsync(RunTemplate t, CancellationToken ct) { lock (_lock) _items[t.Id] = Copy(t); return Task.CompletedTask; }
    public Task<bool> DeleteAsync(Guid id, CancellationToken ct) { lock (_lock) return Task.FromResult(_items.Remove(id)); }
}

public sealed class SqlRunTemplateStore : IRunTemplateStore
{
    private readonly CobaltMrpDbContext _db;
    public SqlRunTemplateStore(CobaltMrpDbContext db) => _db = db;

    public async Task<IReadOnlyList<RunTemplate>> ListAsync(CancellationToken ct) =>
        (await _db.RunTemplates.AsNoTracking().ToListAsync(ct)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    public Task<RunTemplate?> GetAsync(Guid id, CancellationToken ct) => _db.RunTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task SaveAsync(RunTemplate t, CancellationToken ct)
    {
        var row = await _db.RunTemplates.FirstOrDefaultAsync(x => x.Id == t.Id, ct);
        if (row is null) _db.RunTemplates.Add(t);
        else _db.Entry(row).CurrentValues.SetValues(t);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var row = await _db.RunTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return false;
        _db.RunTemplates.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
