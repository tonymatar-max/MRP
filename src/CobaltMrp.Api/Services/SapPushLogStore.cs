using CobaltMrp.Data;
using CobaltMrp.Domain;
using Microsoft.EntityFrameworkCore;

namespace CobaltMrp.Api.Services;

/// <summary>Where the log of writes to SAP B1 lives. Entries are only ever added, never edited or deleted.</summary>
public interface ISapPushLog
{
    Task AddAsync(IEnumerable<SapPushLogEntry> entries, CancellationToken ct);
    /// <summary>Newest first, at most <see cref="SapPushLogQuery.Limit"/> (clamped 1..1000).</summary>
    Task<IReadOnlyList<SapPushLogEntry>> QueryAsync(SapPushLogQuery q, CancellationToken ct);
}

internal static class SapPushLogFilter
{
    public static int Clamp(int limit) => Math.Clamp(limit <= 0 ? 200 : limit, 1, 1000);

    public static IQueryable<SapPushLogEntry> Apply(IQueryable<SapPushLogEntry> src, SapPushLogQuery q)
    {
        if (q.Success is { } ok) src = src.Where(x => x.Success == ok);
        if (!string.IsNullOrWhiteSpace(q.Action)) { var a = q.Action.Trim(); src = src.Where(x => x.Action == a); }
        if (!string.IsNullOrWhiteSpace(q.Item)) { var i = q.Item.Trim(); src = src.Where(x => x.ItemCode != null && x.ItemCode.Contains(i)); }
        if (!string.IsNullOrWhiteSpace(q.User)) { var u = q.User.Trim(); src = src.Where(x => x.UserName.Contains(u)); }
        if (q.From is { } from) src = src.Where(x => x.At >= from);
        if (q.To is { } to) src = src.Where(x => x.At <= to);
        return src;
    }
}

/// <summary>Used while no Cobalt database is configured. Lost on restart (the Event Log still has each line).</summary>
public sealed class MemorySapPushLog : ISapPushLog
{
    private readonly List<SapPushLogEntry> _items = new();
    private readonly object _lock = new();

    public Task AddAsync(IEnumerable<SapPushLogEntry> entries, CancellationToken ct)
    {
        lock (_lock) _items.AddRange(entries);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SapPushLogEntry>> QueryAsync(SapPushLogQuery q, CancellationToken ct)
    {
        List<SapPushLogEntry> snapshot;
        lock (_lock) snapshot = _items.ToList();
        // Ordered after filtering: DateTimeOffset ordering is not translated by every provider, so do it in memory here.
        var rows = SapPushLogFilter.Apply(snapshot.AsQueryable(), q).OrderByDescending(x => x.At).Take(SapPushLogFilter.Clamp(q.Limit)).ToList();
        return Task.FromResult<IReadOnlyList<SapPushLogEntry>>(rows);
    }
}

public sealed class SqlSapPushLog : ISapPushLog
{
    private readonly CobaltMrpDbContext _db;
    public SqlSapPushLog(CobaltMrpDbContext db) => _db = db;

    public async Task AddAsync(IEnumerable<SapPushLogEntry> entries, CancellationToken ct)
    {
        _db.SapPushLog.AddRange(entries);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SapPushLogEntry>> QueryAsync(SapPushLogQuery q, CancellationToken ct)
    {
        var rows = await SapPushLogFilter.Apply(_db.SapPushLog.AsNoTracking(), q).ToListAsync(ct);
        // SQL Server orders DATETIMEOFFSET fine, but this keeps the order identical to the memory store.
        return rows.OrderByDescending(x => x.At).Take(SapPushLogFilter.Clamp(q.Limit)).ToList();
    }
}

/// <summary>Who is pushing and why, passed down so each write to SAP B1 can be logged.</summary>
public sealed record PushContext(ISapPushLog Log, string User, Guid? RunId = null, Guid? ExceptionId = null);

/// <summary>
/// Writes log entries without ever failing the caller: a push to SAP B1 that already happened must still be reported
/// even when the log itself cannot be saved (the warning below then holds the only record).
/// </summary>
public static class SapPushRecorder
{
    private const int MaxError = 4000;

    public static async Task RecordAsync(PushContext? ctx, ILogger log, IReadOnlyList<SapPushLogEntry> entries)
    {
        if (entries.Count == 0) return;
        foreach (var e in entries)
        {
            if (ctx is not null) { e.UserName = ctx.User; e.RunId ??= ctx.RunId; e.ExceptionId ??= ctx.ExceptionId; }
            if (e.Error is { Length: > MaxError }) e.Error = e.Error[..MaxError];
            if (e.Summary.Length > 500) e.Summary = e.Summary[..500];
            // The Event Log keeps a line for every push too (the log screen reads the database).
            if (e.Success) log.LogInformation("SAP B1 push OK: {Action} {Item} by {User}: {Summary} ({Target})", e.Action, e.ItemCode, e.UserName, e.Summary, e.Target);
            else log.LogWarning("SAP B1 push FAILED: {Action} {Item} by {User}: {Summary}: {Error}", e.Action, e.ItemCode, e.UserName, e.Summary, e.Error);
        }
        if (ctx is null) return;
        try { await ctx.Log.AddAsync(entries, CancellationToken.None); }
        catch (Exception ex) { log.LogError(ex, "Could not save {Count} SAP B1 push log entries to the database", entries.Count); }
    }
}
