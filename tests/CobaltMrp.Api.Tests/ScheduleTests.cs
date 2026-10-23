using System.Text.Json;
using CobaltMrp.Api.Services;
using CobaltMrp.Api.Settings;
using CobaltMrp.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CobaltMrp.Api.Tests;

/// <summary>Several scheduled runs, each tied to a saved Run MRP template.</summary>
public class ScheduleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cobalt-sched-test-" + Guid.NewGuid().ToString("N"));
    public ScheduleTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SettingsService Service() => new(_dir, new SecretProtector(new EphemeralDataProtectionProvider()), NullLogger<SettingsService>.Instance);
    private static string Tpl() => Guid.NewGuid().ToString();
    private static ScheduleSettings Sch(string id, string time = "02:00", string[]? days = null, bool enabled = true, string? tpl = null) =>
        new() { Id = id, Enabled = enabled, Time = time, Days = (days ?? ScheduleSettings.AllDays).ToList(), TemplateId = tpl ?? Tpl() };

    private SettingsService With(params ScheduleSettings[] list)
    {
        var s = Service(); var a = s.Get(); a.Schedules = list.ToList(); s.Save(a); return s;
    }

    private sealed class FakeJob : IScheduledJob
    {
        public List<string> Fired { get; } = new();
        public HashSet<string> Throw { get; } = new();
        public Task<ScheduledOutcome> RunAsync(ScheduleSettings s, CancellationToken ct)
        {
            if (Throw.Contains(s.Id)) throw new InvalidOperationException("boom");
            Fired.Add(s.Id); return Task.FromResult(new ScheduledOutcome(true, "ok", Guid.NewGuid()));
        }
    }

    // ------------------------------------------------------------ migration

    [Fact]
    public void AnOldSingleSchedule_IsMovedIntoTheList_Once_AndNeverDuplicated()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            "{\"schedule\":{\"enabled\":true,\"time\":\"03:30\",\"days\":[\"Mon\",\"Fri\"],\"templateId\":\"" + "11111111-1111-1111-1111-111111111111" + "\"}}");

        var first = Service().Get();
        var one = Assert.Single(first.Schedules!);
        Assert.True(one.Enabled); Assert.Equal("03:30", one.Time); Assert.Equal(new[] { "Mon", "Fri" }, one.Days);
        Assert.Equal("11111111-1111-1111-1111-111111111111", one.TemplateId); Assert.Equal(ScheduleSettings.MigratedId, one.Id);
        Assert.Null(first.Schedule);

        var onDisk = File.ReadAllText(Path.Combine(_dir, "settings.json"));               // saved once: the old field is gone
        using (var doc = JsonDocument.Parse(onDisk)) Assert.False(doc.RootElement.TryGetProperty("schedule", out _));

        Assert.Single(Service().Get().Schedules!);                                         // load again and again: still one
        Assert.Single(Service().Get().Schedules!);
    }

    [Fact]
    public void AnUntouchedOldSchedule_MigratesToNothing_AndAnOldClientCannotWipeTheList()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{\"schedule\":{\"enabled\":false,\"time\":\"02:00\",\"days\":[\"Mon\",\"Tue\",\"Wed\",\"Thu\",\"Fri\",\"Sat\",\"Sun\"],\"templateId\":\"\"}}");
        var s = Service();
        Assert.Empty(s.Get().Schedules!);

        var a = s.Get(); a.Schedules = new() { Sch("keep") }; s.Save(a);
        var old = s.Get(); old.Schedules = null;                                            // an old client sends no list at all
        old.Schedule = new ScheduleSettings { Enabled = true, Time = "05:00" };
        s.Save(old);
        Assert.Equal("keep", Assert.Single(s.Get().Schedules!).Id);                         // the stored list is kept
        Assert.Null(s.Get().Schedule);
    }

    [Fact]
    public void TheOldStateFile_IsAdoptedByTheMigratedSchedule()
    {
        File.WriteAllText(Path.Combine(_dir, "automation-state.json"),
            "{\"lastRunAt\":\"2026-10-14T02:00:05+00:00\",\"lastRunOk\":true,\"lastRunMessage\":\"Run finished\",\"lastScheduledDate\":\"2026-10-14\",\"lastNoticeOk\":true}");
        var st = new AutomationState(_dir).Get();
        var d = st.Schedules[ScheduleSettings.MigratedId];
        Assert.Equal("2026-10-14", d.LastScheduledDate); Assert.True(d.LastOk); Assert.Equal("Run finished", d.LastMessage);
        Assert.True(st.LastNoticeOk); Assert.Null(st.LastScheduledDate);
    }

    // ------------------------------------------------------------ validation and normalisation

    [Fact]
    public void Validation_RefusesWithAClearMessage()
    {
        var none = new List<ScheduleSettings>();
        Assert.Contains("HH:mm", ScheduleSettings.Validate(new() { Sch("a", time: "25:99") }, none));
        Assert.Contains("HH:mm", ScheduleSettings.Validate(new() { Sch("a", time: "2:00") }, none));
        Assert.Contains("at least one day", ScheduleSettings.Validate(new() { Sch("a", days: Array.Empty<string>()) }, none));
        Assert.Contains("choose a template", ScheduleSettings.Validate(new() { new ScheduleSettings { Id = "a", Enabled = true } }, none));
        Assert.Contains("days must be", ScheduleSettings.Validate(new() { Sch("a", days: new[] { "Xyz" }) }, none));
        Assert.Contains("At most 20", ScheduleSettings.Validate(Enumerable.Range(0, 21).Select(i => Sch("s" + i)).ToList(), none));
        Assert.Null(ScheduleSettings.Validate(Enumerable.Range(0, 20).Select(i => Sch("s" + i)).ToList(), none));
        Assert.Contains("Nightly", ScheduleSettings.Validate(new() { new ScheduleSettings { Id = "a", Name = "Nightly", Time = "xx" } }, none));
    }

    [Fact]
    public void Validation_AllowsAnUnknownTemplate_ADisabledScheduleWithoutOne_AndAnOldEnabledOneUntilItIsEdited()
    {
        Assert.Null(ScheduleSettings.Validate(new() { Sch("a", tpl: Tpl()) }, new()));                                   // unknown template: saved, flagged later
        Assert.Null(ScheduleSettings.Validate(new() { new ScheduleSettings { Id = "a", Enabled = false } }, new()));   // draft
        var old = new ScheduleSettings { Id = "default", Enabled = true };                                              // pre-template schedule
        Assert.Null(ScheduleSettings.Validate(new() { old.Clone() }, new() { old }));
        Assert.NotNull(ScheduleSettings.Validate(new() { new ScheduleSettings { Id = "other", Enabled = true } }, new() { old }));
    }

    [Fact]
    public void Normalisation_GivesEveryScheduleAUniqueId_AndCapsTheList()
    {
        var s = Service(); var a = s.Get();
        a.Schedules = new() { Sch(""), Sch("dup"), Sch("dup"), new ScheduleSettings { Id = "x", Name = new string('n', 90), Time = "7:5", Enabled = true, Days = new() { "monday", "fri", "fri" } } };
        a.Schedules.AddRange(Enumerable.Range(0, 30).Select(i => Sch("m" + i)));
        s.Save(a);
        var got = s.Get().Schedules!;
        Assert.Equal(20, got.Count);
        Assert.Equal(got.Count, got.Select(g => g.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(got, g => Assert.NotEmpty(g.Id));
        var x = got.Single(g => g.Id == "x");
        Assert.Equal(60, x.Name.Length); Assert.Equal("02:00", x.Time); Assert.Equal(new[] { "Mon", "Fri" }, x.Days);
        Assert.Equal(got.Select(g => g.Id), Service().Get().Schedules!.Select(g => g.Id));                           // survives a reload
    }

    // ------------------------------------------------------------ due-time evaluation across schedules

    [Fact]
    public async Task SeveralSchedules_EachFireOnTheirOwnTimeAndDays_Once()
    {
        var clock = new DateTime(2026, 10, 14, 1, 0, 0);                                                                  // a Wednesday
        var s = With(Sch("night", "02:00"), Sch("noon", "12:00"), Sch("monfri", "02:00", new[] { "Mon", "Fri" }),
                     Sch("off", "02:00", enabled: false), Sch("sameTime", "02:00"));
        var job = new FakeJob(); var state = new AutomationState(null);
        var t = new ScheduleTicker(s, state, job, NullLogger.Instance, () => clock);

        Assert.False(await t.TickAsync(default));
        clock = new DateTime(2026, 10, 14, 2, 0, 0);
        Assert.True(await t.TickAsync(default));
        Assert.Equal(new[] { "night", "sameTime" }, job.Fired);                                                           // same time: both, in list order; Mon/Fri and off do not
        clock = new DateTime(2026, 10, 14, 2, 5, 0);
        Assert.False(await t.TickAsync(default)); Assert.Equal(2, job.Fired.Count);                                       // never twice that day
        clock = new DateTime(2026, 10, 14, 12, 0, 0);
        Assert.True(await t.TickAsync(default));
        Assert.Equal(new[] { "night", "sameTime", "noon" }, job.Fired);
        clock = new DateTime(2026, 10, 16, 2, 0, 0);                                                                      // Friday
        Assert.True(await t.TickAsync(default));
        Assert.Equal(new[] { "night", "sameTime", "noon", "night", "monfri", "sameTime" }, job.Fired);
    }

    [Fact]
    public async Task AScheduleThatThrows_DoesNotStopTheOthers_AndIsNotRetriedThatDay()
    {
        var clock = new DateTime(2026, 10, 14, 3, 0, 0);
        var s = With(Sch("bad"), Sch("good"));
        var job = new FakeJob(); job.Throw.Add("bad");
        var t = new ScheduleTicker(s, new AutomationState(null), job, NullLogger.Instance, () => clock);
        Assert.True(await t.TickAsync(default));
        Assert.Equal(new[] { "good" }, job.Fired);
        job.Throw.Clear(); clock = clock.AddMinutes(1);
        Assert.False(await t.TickAsync(default));                                                                         // no retry storm
        Assert.Equal(new[] { "good" }, job.Fired);
    }

    [Fact]
    public async Task EachScheduleKeepsItsOwnMissedAndHandledState_AndRemovedSchedulesAreForgotten()
    {
        var clock = new DateTime(2026, 10, 14, 15, 0, 0);
        var s = With(Sch("early", "02:00"), Sch("recent", "14:00"));
        var job = new FakeJob(); var state = new AutomationState(_dir);
        var t = new ScheduleTicker(s, state, job, NullLogger.Instance, () => clock);
        Assert.True(await t.TickAsync(default));
        Assert.Equal(new[] { "recent" }, job.Fired);                                                                       // 02:00 missed by 13h: skipped; 14:00 within 6h: runs
        var early = state.Get().Schedules["early"];
        Assert.False(early.LastOk); Assert.Contains("missed", early.LastMessage);

        var a = s.Get(); a.Schedules = a.Schedules!.Where(x => x.Id == "recent").ToList(); s.Save(a);
        await t.TickAsync(default);
        Assert.DoesNotContain("early", state.Get().Schedules.Keys);
    }

    [Fact]
    public void NextRun_IsPerSchedule()
    {
        var a = Sch("a", "02:00", new[] { "Fri" }); var b = Sch("b", "18:00", new[] { "Wed" }); var c = Sch("c", enabled: false);
        var t = new ScheduleTicker(With(a, b, c), new AutomationState(null), new FakeJob(), NullLogger.Instance, () => new DateTime(2026, 10, 14, 9, 0, 0));
        var info = new AutomationInfo();
        Assert.Equal(new DateTime(2026, 10, 16, 2, 0, 0), t.NextRun(a, info));
        Assert.Equal(new DateTime(2026, 10, 14, 18, 0, 0), t.NextRun(b, info));
        Assert.Null(t.NextRun(c, info));
        info.Schedules["b"] = new ScheduleStatus { LastScheduledDate = "2026-10-14" };
        Assert.Equal(new DateTime(2026, 10, 21, 18, 0, 0), t.NextRun(b, info));
    }

    // ------------------------------------------------------------ overlap protection

    [Fact]
    public async Task TheSameScheduleNeverOverlapsItself_AndDifferentSchedulesAreSerialised()
    {
        var gate = new ScheduleGate();
        var release = new TaskCompletionSource(); var started = new TaskCompletionSource();
        int running = 0, maxRunning = 0;
        async Task<int> Work(TaskCompletionSource? signal, TaskCompletionSource? hold)
        {
            var n = Interlocked.Increment(ref running); maxRunning = Math.Max(maxRunning, n);
            signal?.TrySetResult();
            if (hold is not null) await hold.Task;
            Interlocked.Decrement(ref running);
            return 1;
        }

        var first = gate.TryRunAsync("A", () => Work(started, release), default);
        await started.Task;
        Assert.True(gate.IsActive("A"));
        var again = await gate.TryRunAsync("A", () => Work(null, null), default);
        Assert.False(again.Entered);                                                                                       // refused at once

        var other = gate.TryRunAsync("B", () => Work(null, null), default);                                                // waits for A
        await Task.Delay(100);
        Assert.False(other.IsCompleted);
        release.SetResult();
        Assert.True((await first).Entered); Assert.True((await other).Entered);
        Assert.Equal(1, maxRunning);                                                                                       // never two at once
        Assert.False(gate.IsActive("A")); Assert.False(gate.IsActive("B"));
        Assert.True((await gate.TryRunAsync("A", () => Work(null, null), default)).Entered);                              // free again afterwards
    }

    [Fact]
    public async Task AFailingRun_ReleasesTheGate()
    {
        var gate = new ScheduleGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.TryRunAsync<int>("A", () => throw new InvalidOperationException(), default));
        Assert.False(gate.IsActive("A"));
        Assert.True((await gate.TryRunAsync("B", () => Task.FromResult(1), default)).Entered);
    }

    // ------------------------------------------------------------ status persistence

    [Fact]
    public void TheStatusIsKeptInTheStateFile_NotInSettings_AndSurvivesARestart()
    {
        var s = With(Sch("a"));
        var state = new AutomationState(_dir); var rid = Guid.NewGuid();
        state.UpdateSchedule("a", x => { x.LastFiredAt = DateTimeOffset.UtcNow; x.LastOk = false; x.LastMessage = "Skipped: nope"; x.LastRunId = rid; x.LastScheduledDate = "2026-10-14"; });
        var again = new AutomationState(_dir).Get().Schedules["a"];
        Assert.False(again.LastOk); Assert.Equal("Skipped: nope", again.LastMessage); Assert.Equal(rid, again.LastRunId);
        var settingsJson = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        Assert.DoesNotContain("Skipped: nope", settingsJson); Assert.DoesNotContain("lastFiredAt", settingsJson, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(s.Get().Schedules);
    }

    // ------------------------------------------------------------ the job: skipped schedules and naming

    private (ScheduledJob Job, AutomationState State, MemoryRunTemplateStore Templates) MakeJob()
    {
        var templates = new MemoryRunTemplateStore();
        var services = new ServiceCollection().AddSingleton<IRunTemplateStore>(templates).BuildServiceProvider();
        var state = new AutomationState(null);
        return (new ScheduledJob(services.GetRequiredService<IServiceScopeFactory>(), state, new ScheduleGate(), NullLogger<ScheduledJob>.Instance), state, templates);
    }

    [Fact]
    public async Task ADeletedTemplate_IsSkippedWithAClearFailedStatus_NotACrash()
    {
        var (job, state, _) = MakeJob();
        var o = await job.RunAsync(Sch("a", tpl: Tpl()), default);
        Assert.False(o.Ok); Assert.Null(o.RunId); Assert.Contains("no longer exists", o.Message);
        var st = state.Get().Schedules["a"];
        Assert.False(st.LastOk); Assert.Contains("no longer exists", st.LastMessage); Assert.NotNull(st.LastFiredAt);
    }

    [Fact]
    public async Task TemplateOptionsThatNoLongerValidate_AreSkippedWithTheReason()
    {
        var (job, state, templates) = MakeJob();
        var bad = new RunCriteria { Source = "nonsense" };
        var t = new RunTemplate { Name = "Broken", CriteriaJson = bad.ToJson() };
        await templates.SaveAsync(t, default);
        var o = await job.RunAsync(Sch("a", tpl: t.Id.ToString()), default);
        Assert.False(o.Ok); Assert.Contains("Broken", o.Message); Assert.Contains("Source must be", o.Message);
        Assert.Contains("Skipped", state.Get().Schedules["a"].LastMessage);

        var unreadable = new RunTemplate { Name = "Garbled", CriteriaJson = "not json" };
        await templates.SaveAsync(unreadable, default);
        Assert.Contains("cannot be read", (await job.RunAsync(Sch("b", tpl: unreadable.Id.ToString()), default)).Message);
    }

    [Fact]
    public async Task ADisabledScheduleWithoutATemplate_ReportsItInsteadOfRunning()
    {
        var (job, _, _) = MakeJob();
        var o = await job.RunAsync(new ScheduleSettings { Id = "a", Enabled = false }, default);
        Assert.False(o.Ok); Assert.Contains("no template", o.Message);
    }

    [Fact]
    public void RunNames_AreTheTemplateNameAndTheLocalMinute()
    {
        Assert.Equal("Nightly full · 2026-10-14 02:00", ScheduledJob.RunName("Nightly full", new DateTime(2026, 10, 14, 2, 0, 41)));
    }

    // ------------------------------------------------------------ alerts compare with the same template

    private sealed class FakeSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = new();
        public Task SendAsync(NotificationSettings s, EmailMessage m, CancellationToken ct) { Sent.Add(m); return Task.CompletedTask; }
    }

    [Fact]
    public async Task ScheduledAlerts_CompareWithThePreviousRunOfTheSameTemplate_NotWithAnyOtherRun()
    {
        var s = Service(); var a = s.Get();
        a.Notifications.Enabled = true; a.Notifications.SmtpHost = "smtp.test"; a.Notifications.From = "m@test.local"; a.Notifications.Recipients = "x@test.local";
        s.Save(a);
        var store = new MemoryRunStore(); var sender = new FakeSender();
        var n = new ExceptionNotifier(s, sender, new AutomationState(null), store, NullLogger<ExceptionNotifier>.Instance);
        static ExceptionMessage Ex(string item) => new() { ItemCode = item, Type = ExceptionType.Expedite, Message = "m", WarehouseCode = "WH01" };
        MrpRun R(DateTimeOffset at, string name, string by, params ExceptionMessage[] ex) { var r = new MrpRun { RunAt = at, Name = name, RunBy = by }; r.Exceptions.AddRange(ex); return r; }
        var t0 = DateTimeOffset.UtcNow.AddDays(-2);

        await store.AddAsync(R(t0, "Alpha · 2026-10-12 02:00", "scheduler", Ex("A1")), default);                         // Alpha, yesterday-ish
        await store.AddAsync(R(t0.AddHours(1), "Beta · 2026-10-12 03:00", "scheduler", Ex("B1"), Ex("B2")), default);     // another template, newer
        var alpha = R(t0.AddDays(1), "Alpha · 2026-10-13 02:00", "scheduler", Ex("A1"), Ex("B1"));
        await store.AddAsync(alpha, default);

        var r = await n.NotifyAsync(alpha, scheduled: true, default, sameTemplateAs: "Alpha");
        Assert.True(r.Sent); Assert.Equal(1, r.NewExceptions);                                                               // only B1 is new vs Alpha's own previous run
        Assert.Contains("B1", sender.Sent.Single().Text); Assert.DoesNotContain("A1", sender.Sent.Single().Text);

        // Compared with "whatever came before" (the old behaviour, still used when no template is given) the answer differs:
        // the run just before is Beta's, so A1 looks new and B1 does not.
        sender.Sent.Clear();
        var r2 = await n.NotifyAsync(alpha, scheduled: true, default);
        Assert.True(r2.Sent); Assert.Contains("A1", sender.Sent.Single().Text); Assert.DoesNotContain("B1", sender.Sent.Single().Text);

        // A template with no earlier run of its own has nothing to compare with: no email.
        var gamma = R(t0.AddDays(1).AddHours(1), "Gamma · 2026-10-13 03:00", "scheduler", Ex("G1")); await store.AddAsync(gamma, default);
        var r3 = await n.NotifyAsync(gamma, true, default, sameTemplateAs: "Gamma");
        Assert.False(r3.Sent); Assert.Contains("first run", r3.Message);
    }
}
