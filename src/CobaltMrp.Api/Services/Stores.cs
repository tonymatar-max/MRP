using CobaltMrp.Data;
using CobaltMrp.Domain;
using Microsoft.EntityFrameworkCore;

namespace CobaltMrp.Api.Services;

public sealed record RunRequest(string[]? Warehouses, int? HorizonDays, int? BucketDays, string? Source, string? Notes = null, RunCriteria? Criteria = null, string? Name = null);
public sealed record PreviewRequest(RunCriteria? Criteria);
public sealed record ReleaseRequest(Guid[] PlannedOrderIds);

public sealed record RunSummary(Guid Id, DateTimeOffset RunAt, string RunBy, DateOnly HorizonStart,
    DateOnly HorizonEnd, int BucketDays, int PlannedOrderCount, int ExceptionCount, int OverloadedBuckets, string? Name = null, string? Notes = null)
{
    public static RunSummary From(MrpRun r) => new(r.Id, r.RunAt, r.RunBy, r.HorizonStart, r.HorizonEnd,
        r.BucketDays, r.PlannedOrders.Count, r.Exceptions.Count, r.CapacityLoads.Count(c => c.Overloaded), r.Name, r.Notes);
}

public sealed record RunDetail(Guid Id, DateTimeOffset RunAt, string RunBy, DateOnly HorizonStart,
    DateOnly HorizonEnd, int BucketDays, int PlannedOrderCount, int ExceptionCount, int OverloadedBuckets,
    List<PlannedOrderDto> PlannedOrders, List<PeggingLink> Pegging, List<ExceptionDto> Exceptions,
    List<CapacityLoad> CapacityLoads, RunCriteria? Criteria, string? Name = null, string? Notes = null)
{
    public RunDetail(RunSummary s, List<PlannedOrderDto> po, List<PeggingLink> pg, List<ExceptionDto> ex,
        List<CapacityLoad> cl, RunCriteria? criteria)
        : this(s.Id, s.RunAt, s.RunBy, s.HorizonStart, s.HorizonEnd, s.BucketDays, s.PlannedOrderCount,
               s.ExceptionCount, s.OverloadedBuckets, po, pg, ex, cl, criteria, s.Name, s.Notes) { }
}

/// <summary>Filters for the Calendar screen. Kinds empty = all kinds.</summary>
public sealed record CalendarQuery(DateOnly From, DateOnly To, IReadOnlyCollection<CalendarEventKind> Kinds,
    string? Item, string? Warehouse, int Limit);

// ---- Runs --------------------------------------------------------------------------------------

public interface IRunStore
{
    Task AddAsync(MrpRun run, CancellationToken ct);
    Task<MrpRun?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<RunSummary>> ListAsync(CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
    /// <summary>Events of one run matching the query, oldest first, at most Limit + 1 (so the caller can tell if it was cut).</summary>
    Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(Guid runId, CalendarQuery q, CancellationToken ct);
}

/// <summary>Used while no Cobalt database is configured. Lost on restart; the UI warns about it.</summary>
public sealed class MemoryRunStore : IRunStore
{
    private readonly List<MrpRun> _runs = new();
    private readonly object _lock = new();
    public Task AddAsync(MrpRun r, CancellationToken ct) { lock (_lock) _runs.Add(r); return Task.CompletedTask; }
    public Task<MrpRun?> GetAsync(Guid id, CancellationToken ct) { lock (_lock) return Task.FromResult(_runs.FirstOrDefault(r => r.Id == id)); }
    public Task<IReadOnlyList<RunSummary>> ListAsync(CancellationToken ct)
    { lock (_lock) return Task.FromResult<IReadOnlyList<RunSummary>>(_runs.OrderByDescending(r => r.RunAt).Select(RunSummary.From).ToList()); }
    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(Guid runId, CalendarQuery q, CancellationToken ct)
    {
        MrpRun? run; lock (_lock) run = _runs.FirstOrDefault(r => r.Id == runId);
        if (run is null) return Task.FromResult<IReadOnlyList<CalendarEvent>>(Array.Empty<CalendarEvent>());
        var kinds = q.Kinds;
        var hits = run.Events.Where(e => e.Date >= q.From && e.Date <= q.To
                && (kinds.Count == 0 || kinds.Contains(e.Kind))
                && (string.IsNullOrWhiteSpace(q.Item) || e.ItemCode.Contains(q.Item, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(q.Warehouse) || string.Equals(e.WarehouseCode, q.Warehouse, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.Date).ThenBy(e => e.Kind).Take(q.Limit + 1).ToList();
        return Task.FromResult<IReadOnlyList<CalendarEvent>>(hits);
    }
}

/// <summary>SQL Server-backed store (the Cobalt MRP database). Runs survive restarts.</summary>
public sealed class SqlRunStore : IRunStore
{
    private readonly CobaltMrpDbContext _db;
    private readonly RunRepository _repo;
    public SqlRunStore(CobaltMrpDbContext db) { _db = db; _repo = new RunRepository(db); }

    public Task AddAsync(MrpRun run, CancellationToken ct) => _repo.SaveRunAsync(run, ct);
    public Task<MrpRun?> GetAsync(Guid id, CancellationToken ct) => _repo.GetRunAsync(id, ct);
    public Task SaveChangesAsync(CancellationToken ct) => _repo.SaveChangesAsync(ct);

    public async Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(Guid runId, CalendarQuery q, CancellationToken ct)
    {
        var query = _db.CalendarEvents.AsNoTracking()
            .Where(e => EF.Property<Guid>(e, "MrpRunId") == runId && e.Date >= q.From && e.Date <= q.To);
        if (q.Kinds.Count > 0) query = query.Where(e => q.Kinds.Contains(e.Kind));
        if (!string.IsNullOrWhiteSpace(q.Item)) query = query.Where(e => e.ItemCode.Contains(q.Item));
        if (!string.IsNullOrWhiteSpace(q.Warehouse)) query = query.Where(e => e.WarehouseCode == q.Warehouse);
        return await query.OrderBy(e => e.Date).ThenBy(e => e.Kind).Take(q.Limit + 1).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RunSummary>> ListAsync(CancellationToken ct)
    {
        var rows = await _db.Runs.AsNoTracking().OrderByDescending(r => r.RunAt).Take(50)
            .Select(r => new
            {
                r.Id, r.RunAt, r.RunBy, r.HorizonStart, r.HorizonEnd, r.BucketDays, r.Name, r.Notes,
                Orders = r.PlannedOrders.Count(), Exc = r.Exceptions.Count(),
                Over = r.CapacityLoads.Count(c => c.RequiredHours > c.AvailableHours),
            }).ToListAsync(ct);
        return rows.Select(r => new RunSummary(r.Id, r.RunAt, r.RunBy, r.HorizonStart, r.HorizonEnd,
            r.BucketDays, r.Orders, r.Exc, r.Over, r.Name, r.Notes)).ToList();
    }
}

// ---- Forecasts ---------------------------------------------------------------------------------

public sealed record ForecastDto(string Id, string ItemCode, string WarehouseCode, DateOnly PeriodStart,
    decimal Quantity, decimal Consumed)
{
    public Forecast ToForecast() => new()
    {
        ItemCode = ItemCode, WarehouseCode = WarehouseCode, PeriodStart = PeriodStart,
        Quantity = Quantity, Consumed = Consumed,
    };
}

public interface IForecastStore
{
    Task<IReadOnlyList<ForecastDto>> ListAsync(CancellationToken ct);
    Task<ForecastDto> UpsertAsync(ForecastDto dto, CancellationToken ct);
}

internal static class ForecastIds
{
    public static string NewId() => $"FC-{Guid.NewGuid():N}"[..11];
    public static IEnumerable<ForecastDto> FromMock(PlanningDataSet data) =>
        data.Forecasts.Select((f, i) => new ForecastDto($"FC-{i + 1:000}", f.ItemCode, f.WarehouseCode,
            f.PeriodStart, f.Quantity, f.Consumed));
}

/// <summary>Fallback without a database: seeded from the mock data, lost on restart.</summary>
public sealed class MemoryForecastStore : IForecastStore
{
    private readonly MockB1DataProvider _seed;
    private readonly Dictionary<string, ForecastDto> _items = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _seeded;
    public MemoryForecastStore(MockB1DataProvider seed) => _seed = seed;

    private async Task EnsureSeeded(CancellationToken ct)
    {
        if (_seeded) return;
        foreach (var f in ForecastIds.FromMock(await _seed.LoadAsync(Array.Empty<string>(), ct))) _items[f.Id] = f;
        _seeded = true;
    }

    public async Task<IReadOnlyList<ForecastDto>> ListAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await EnsureSeeded(ct); return _items.Values.OrderBy(f => f.Id).ToList(); }
        finally { _gate.Release(); }
    }

    public async Task<ForecastDto> UpsertAsync(ForecastDto dto, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureSeeded(ct);
            var saved = string.IsNullOrWhiteSpace(dto.Id) ? dto with { Id = ForecastIds.NewId() } : dto;
            _items[saved.Id] = saved;
            return saved;
        }
        finally { _gate.Release(); }
    }
}

/// <summary>SQL-backed Cobalt-managed forecasts. Starts empty: no demo data is ever written to a real database.</summary>
public sealed class SqlForecastStore : IForecastStore
{
    private readonly CobaltMrpDbContext _db;
    public SqlForecastStore(CobaltMrpDbContext db) => _db = db;

    public async Task<IReadOnlyList<ForecastDto>> ListAsync(CancellationToken ct)
    {
        var rows = await _db.Forecasts.AsNoTracking().OrderBy(f => f.Id).ToListAsync(ct);
        return rows.Select(r => new ForecastDto(r.Id, r.ItemCode, r.WarehouseCode, r.PeriodStart, r.Quantity, r.Consumed)).ToList();
    }

    public async Task<ForecastDto> UpsertAsync(ForecastDto dto, CancellationToken ct)
    {
        var saved = string.IsNullOrWhiteSpace(dto.Id) ? dto with { Id = ForecastIds.NewId() } : dto;
        var row = await _db.Forecasts.FindAsync(new object[] { saved.Id }, ct);
        if (row is null) _db.Forecasts.Add(ToRow(saved));
        else
        {
            row.ItemCode = saved.ItemCode; row.WarehouseCode = saved.WarehouseCode;
            row.PeriodStart = saved.PeriodStart; row.Quantity = saved.Quantity; row.Consumed = saved.Consumed;
        }
        await _db.SaveChangesAsync(ct);
        return saved;
    }

    private static ForecastRow ToRow(ForecastDto d) => new()
    {
        Id = d.Id, ItemCode = d.ItemCode, WarehouseCode = d.WarehouseCode,
        PeriodStart = d.PeriodStart, Quantity = d.Quantity, Consumed = d.Consumed,
    };
}
