using CobaltMrp.Api.Services;
using CobaltMrp.Api.Settings;
using CobaltMrp.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CobaltMrp.Api.Tests;

public class SapPushLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cobalt-saplog-test-" + Guid.NewGuid().ToString("N"));
    public SapPushLogTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static SapPushLogEntry E(string action, bool ok, string item, string user, DateTimeOffset at) =>
        new() { Action = action, Success = ok, ItemCode = item, UserName = user, At = at, Summary = "s", Error = ok ? null : "boom" };

    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private static async Task<MemorySapPushLog> Seeded()
    {
        var log = new MemorySapPushLog();
        await log.AddAsync(new[]
        {
            E(SapPushActions.CreatePurchaseOrder, true, "ITM-01", "tony", T0),
            E(SapPushActions.CreatePurchaseOrder, false, "ITM-02", "tony", T0.AddMinutes(1)),
            E(SapPushActions.MoveDueDate, true, "ITM-01", "sara", T0.AddMinutes(2)),
            E(SapPushActions.CloseDocument, false, "ITM-03", "sara", T0.AddMinutes(3)),
        }, CancellationToken.None);
        return log;
    }

    [Fact]
    public async Task Query_IsNewestFirst_AndFiltersCombine()
    {
        var log = await Seeded();
        var all = await log.QueryAsync(new SapPushLogQuery(), CancellationToken.None);
        Assert.Equal(new[] { "ITM-03", "ITM-01", "ITM-02", "ITM-01" }, all.Select(x => x.ItemCode));

        Assert.Equal(2, (await log.QueryAsync(new SapPushLogQuery(Success: false), CancellationToken.None)).Count);
        Assert.Equal(2, (await log.QueryAsync(new SapPushLogQuery(User: "sara"), CancellationToken.None)).Count);
        Assert.Single(await log.QueryAsync(new SapPushLogQuery(Action: SapPushActions.MoveDueDate), CancellationToken.None));
        Assert.Equal(2, (await log.QueryAsync(new SapPushLogQuery(Item: "ITM-01"), CancellationToken.None)).Count);
        var failedBySara = await log.QueryAsync(new SapPushLogQuery(Success: false, User: "sara"), CancellationToken.None);
        Assert.Equal("ITM-03", Assert.Single(failedBySara).ItemCode);
        var window = await log.QueryAsync(new SapPushLogQuery(From: T0.AddMinutes(1), To: T0.AddMinutes(2)), CancellationToken.None);
        Assert.Equal(2, window.Count);
    }

    [Fact]
    public async Task Query_LimitIsClamped()
    {
        var log = await Seeded();
        Assert.Single(await log.QueryAsync(new SapPushLogQuery(Limit: 1), CancellationToken.None));
        Assert.Equal(4, (await log.QueryAsync(new SapPushLogQuery(Limit: 0), CancellationToken.None)).Count);   // 0 -> default
        Assert.Equal(4, (await log.QueryAsync(new SapPushLogQuery(Limit: 99999), CancellationToken.None)).Count);
    }

    private sealed class ThrowingLog : ISapPushLog
    {
        public Task AddAsync(IEnumerable<SapPushLogEntry> entries, CancellationToken ct) => throw new InvalidOperationException("db down");
        public Task<IReadOnlyList<SapPushLogEntry>> QueryAsync(SapPushLogQuery q, CancellationToken ct) => throw new InvalidOperationException("db down");
    }

    [Fact]
    public async Task Recorder_NeverThrows_WhenTheLogCannotBeSaved()
    {
        var ctx = new PushContext(new ThrowingLog(), "tony");
        await SapPushRecorder.RecordAsync(ctx, NullLogger.Instance, new[] { E("X", true, "I", "", T0) });   // must not throw
    }

    [Fact]
    public async Task Recorder_StampsUserRunAndException_AndTrimsLongErrors()
    {
        var log = new MemorySapPushLog();
        var run = Guid.NewGuid(); var ex = Guid.NewGuid();
        var e = E("X", false, "I", "", T0); e.Error = new string('x', 9000);
        await SapPushRecorder.RecordAsync(new PushContext(log, "tony", run, ex), NullLogger.Instance, new[] { e });
        var row = Assert.Single(await log.QueryAsync(new SapPushLogQuery(), CancellationToken.None));
        Assert.Equal("tony", row.UserName);
        Assert.Equal(run, row.RunId);
        Assert.Equal(ex, row.ExceptionId);
        Assert.Equal(4000, row.Error!.Length);
    }

    [Fact]
    public async Task Recorder_WithoutContext_DoesNothing()
    {
        await SapPushRecorder.RecordAsync(null, NullLogger.Instance, new[] { E("X", true, "I", "", T0) });
    }

    private SettingsService Settings(bool enabled)
    {
        var s = new SettingsService(_dir, new SecretProtector(new EphemeralDataProtectionProvider()), NullLogger<SettingsService>.Instance);
        var a = s.Get();
        a.ServiceLayer.Enabled = enabled;
        a.ServiceLayer.BaseUrl = "http://127.0.0.1:1/b1s/v2";     // nothing listens here: the session fails fast
        a.ServiceLayer.CompanyDb = "TEST"; a.ServiceLayer.UserName = "manager"; a.ServiceLayer.Password = "x";
        s.Save(a);
        return s;
    }

    private static PlannedOrder Order() => new() { ItemCode = "ITM-01", WarehouseCode = "WH01", Type = PlannedOrderType.PurchaseOrder, Quantity = 5, DueDate = new DateOnly(2026, 11, 1), ReleaseDate = new DateOnly(2026, 10, 20) };

    [Fact]
    public async Task Release_WithWritebackOff_LogsNothing_BecauseNothingWasSent()
    {
        var log = new MemorySapPushLog();
        var release = new ReleaseService(Settings(enabled: false), NullLogger<ReleaseService>.Instance);
        var outcome = await release.ReleaseAsync(new MemoryRunStore(), new[] { Order() }, CancellationToken.None, new PushContext(log, "tony"));
        Assert.False(outcome.WritebackEnabled);
        Assert.Empty(await log.QueryAsync(new SapPushLogQuery(), CancellationToken.None));
    }

    [Fact]
    public async Task Release_WhenTheSessionFails_LogsOneFailureWithTheUser()
    {
        var log = new MemorySapPushLog();
        var release = new ReleaseService(Settings(enabled: true), NullLogger<ReleaseService>.Instance);
        var runId = Guid.NewGuid();
        var outcome = await release.ReleaseAsync(new MemoryRunStore(), new[] { Order() }, CancellationToken.None, new PushContext(log, "tony", runId));
        Assert.Single(outcome.Failures);
        var row = Assert.Single(await log.QueryAsync(new SapPushLogQuery(), CancellationToken.None));
        Assert.Equal(SapPushActions.Session, row.Action);
        Assert.False(row.Success);
        Assert.Equal("tony", row.UserName);
        Assert.Equal(runId, row.RunId);
        Assert.False(string.IsNullOrWhiteSpace(row.Error));
    }
}
