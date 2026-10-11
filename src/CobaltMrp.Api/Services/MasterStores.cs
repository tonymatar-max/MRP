using CobaltMrp.Data;
using CobaltMrp.Domain;
using Microsoft.EntityFrameworkCore;

namespace CobaltMrp.Api.Services;

/// <summary>Where the planner's overrides live (Cobalt-side only; SAP B1 is never written).</summary>
public interface IMasterStore
{
    Task<IReadOnlyDictionary<string, ItemOverride>> GetItemOverridesAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<string, SupplierSetting>> GetSupplierSettingsAsync(CancellationToken ct);
    /// <summary>Insert or replace; an override with nothing set is deleted instead.</summary>
    Task SaveItemsAsync(IEnumerable<ItemOverride> items, CancellationToken ct);
    Task DeleteItemAsync(string itemCode, CancellationToken ct);
    Task SaveSupplierAsync(SupplierSetting supplier, CancellationToken ct);
}

/// <summary>Used while no Cobalt database is configured. Lost on restart; the UI warns about it.</summary>
public sealed class MemoryMasterStore : IMasterStore
{
    private readonly Dictionary<string, ItemOverride> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SupplierSetting> _suppliers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public Task<IReadOnlyDictionary<string, ItemOverride>> GetItemOverridesAsync(CancellationToken ct)
    { lock (_lock) return Task.FromResult<IReadOnlyDictionary<string, ItemOverride>>(
        _items.ToDictionary(k => k.Key, k => k.Value.Clone(), StringComparer.OrdinalIgnoreCase)); }

    public Task<IReadOnlyDictionary<string, SupplierSetting>> GetSupplierSettingsAsync(CancellationToken ct)
    { lock (_lock) return Task.FromResult<IReadOnlyDictionary<string, SupplierSetting>>(
        _suppliers.ToDictionary(k => k.Key, k => k.Value.Clone(), StringComparer.OrdinalIgnoreCase)); }

    public Task SaveItemsAsync(IEnumerable<ItemOverride> items, CancellationToken ct)
    {
        lock (_lock)
            foreach (var i in items)
                if (i.IsEmpty && string.IsNullOrWhiteSpace(i.Notes)) _items.Remove(i.ItemCode);
                else _items[i.ItemCode] = i.Clone();
        return Task.CompletedTask;
    }

    public Task DeleteItemAsync(string itemCode, CancellationToken ct) { lock (_lock) _items.Remove(itemCode); return Task.CompletedTask; }

    public Task SaveSupplierAsync(SupplierSetting s, CancellationToken ct)
    {
        lock (_lock)
            if (s.IsEmpty) _suppliers.Remove(s.CardCode); else _suppliers[s.CardCode] = s.Clone();
        return Task.CompletedTask;
    }
}

/// <summary>SQL Server-backed overrides (the Cobalt MRP database). Survive restarts.</summary>
public sealed class SqlMasterStore : IMasterStore
{
    private readonly CobaltMrpDbContext _db;
    public SqlMasterStore(CobaltMrpDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<string, ItemOverride>> GetItemOverridesAsync(CancellationToken ct) =>
        (await _db.ItemOverrides.AsNoTracking().ToListAsync(ct)).ToDictionary(i => i.ItemCode, StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyDictionary<string, SupplierSetting>> GetSupplierSettingsAsync(CancellationToken ct) =>
        (await _db.SupplierSettings.AsNoTracking().ToListAsync(ct)).ToDictionary(i => i.CardCode, StringComparer.OrdinalIgnoreCase);

    public async Task SaveItemsAsync(IEnumerable<ItemOverride> items, CancellationToken ct)
    {
        var list = items.ToList();
        var codes = list.Select(i => i.ItemCode).ToList();
        var existing = await _db.ItemOverrides.Where(i => codes.Contains(i.ItemCode)).ToDictionaryAsync(i => i.ItemCode, StringComparer.OrdinalIgnoreCase, ct);
        foreach (var i in list)
        {
            existing.TryGetValue(i.ItemCode, out var row);
            if (i.IsEmpty && string.IsNullOrWhiteSpace(i.Notes)) { if (row is not null) _db.ItemOverrides.Remove(row); continue; }
            if (row is null) _db.ItemOverrides.Add(i.Clone());
            else _db.Entry(row).CurrentValues.SetValues(i);
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteItemAsync(string itemCode, CancellationToken ct)
    {
        var row = await _db.ItemOverrides.FindAsync(new object[] { itemCode }, ct);
        if (row is not null) { _db.ItemOverrides.Remove(row); await _db.SaveChangesAsync(ct); }
    }

    public async Task SaveSupplierAsync(SupplierSetting s, CancellationToken ct)
    {
        var row = await _db.SupplierSettings.FindAsync(new object[] { s.CardCode }, ct);
        if (s.IsEmpty) { if (row is not null) _db.SupplierSettings.Remove(row); }
        else if (row is null) _db.SupplierSettings.Add(s.Clone());
        else _db.Entry(row).CurrentValues.SetValues(s);
        await _db.SaveChangesAsync(ct);
    }
}
