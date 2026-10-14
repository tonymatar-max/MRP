using CobaltMrp.Api.Services;
using CobaltMrp.Api.Settings;
using CobaltMrp.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CobaltMrp.Api.Tests;

/// <summary>New-exception emails and the schedule's decision logic.</summary>
public class AutomationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cobalt-auto-test-" + Guid.NewGuid().ToString("N"));
    public AutomationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeSender : IEmailSender
    {
        public List<(NotificationSettings Settings, EmailMessage Message)> Sent { get; } = new();
        public Exception? Fail { get; set; }
        public Task SendAsync(NotificationSettings settings, EmailMessage message, CancellationToken ct)
        {
            if (Fail is not null) throw Fail;
            Sent.Add((settings, message));
            return Task.CompletedTask;
        }
    }

    private SettingsService Settings(Action<AppSettings>? edit = null)
    {
        var s = new SettingsService(_dir, new SecretProtector(new EphemeralDataProtectionProvider()), NullLogger<SettingsService>.Instance);
        var a = s.Get();
        a.Notifications.Enabled = true; a.Notifications.SmtpHost = "smtp.test"; a.Notifications.From = "mrp@test.local";
        a.Notifications.Recipients = "a@test.local; b@test.local"; a.Notifications.PublicUrl = "https://mrp.test/";
        a.Notifications.Password = "smtp-secret";
        edit?.Invoke(a);
        s.Save(a);
        return s;
    }

    private static ExceptionMessage Ex(string item, ExceptionType t = ExceptionType.Expedite, string msg = "Move in", string? doc = null) =>
        new() { ItemCode = item, Type = t, Message = msg, WarehouseCode = "WH01", SourceDocKey = doc };

    private static MrpRun Run(DateTimeOffset at, string? name, params ExceptionMessage[] ex)
    {
        var r = new MrpRun { RunAt = at, Name = name };
        r.Exceptions.AddRange(ex);
        return r;
    }

    private static async Task<(ExceptionNotifier N, FakeSender Sender, AutomationState State, MemoryRunStore Store)> Make(SettingsService s)
    {
        var sender = new FakeSender(); var state = new AutomationState(null); var store = new MemoryRunStore();
        return (new ExceptionNotifier(s, sender, state, store, NullLogger<ExceptionNotifier>.Instance), sender, state, store);
    }

    // ---------------------------------------------------------------- notifier

    [Fact]
    public async Task NotConfigured_SendsNothing()
    {
        var (n, sender, _, _) = await Make(Settings(a => a.Notifications.Enabled = false));
        var r = await n.NotifyAsync(Run(DateTimeOffset.UtcNow, null, Ex("A")), scheduled: true, default);
        Assert.False(r.Sent); Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task FirstRun_HasNothingToCompareWith()
    {
        var (n, sender, _, store) = await Make(Settings());
        var run = Run(DateTimeOffset.UtcNow, null, Ex("A")); await store.AddAsync(run, default);
        var r = await n.NotifyAsync(run, true, default);
        Assert.False(r.Sent); Assert.Contains("first run", r.Message); Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task NewExceptions_AreEmailed_WithCountsItemsAndLink()
    {
        var (n, sender, state, store) = await Make(Settings());
        var old = Run(DateTimeOffset.UtcNow.AddDays(-1), null, Ex("KEEP"), Ex("GONE"));
        var now = Run(DateTimeOffset.UtcNow, "Nightly", Ex("KEEP"), Ex("NEW1"), Ex("NEW2", ExceptionType.PastDue, "Overdue <b>demand</b>"));
        await store.AddAsync(old, default); await store.AddAsync(now, default);

        var r = await n.NotifyAsync(now, scheduled: true, default);

        Assert.True(r.Sent); Assert.Equal(2, r.NewExceptions);
        var (cfg, msg) = Assert.Single(sender.Sent);
        Assert.Equal(new[] { "a@test.local", "b@test.local" }, cfg.RecipientList());
        Assert.Equal(new[] { "a@test.local", "b@test.local" }, msg.To);                    // the message itself must carry the recipients
        Assert.Contains("2 new exception(s)", msg.Subject); Assert.Contains("Nightly", msg.Subject);
        Assert.Contains("NEW1", msg.Text); Assert.Contains("NEW2", msg.Text); Assert.DoesNotContain("KEEP", msg.Text);
        Assert.Contains("https://mrp.test/exceptions", msg.Text);                        // trailing slash trimmed by the settings
        Assert.Contains("&lt;b&gt;demand&lt;/b&gt;", msg.Html);                           // user data is HTML-encoded
        Assert.DoesNotContain("<b>demand</b>", msg.Html);
        Assert.True(state.Get().LastNoticeOk); Assert.Equal(2, state.Get().LastNoticeNewExceptions);
    }

    [Fact]
    public async Task NoNewExceptions_NoEmail_AndDoneOnesDoNotCount()
    {
        var (n, sender, _, store) = await Make(Settings());
        var done = Ex("D"); done.ActionStatus = "Done";
        var old = Run(DateTimeOffset.UtcNow.AddDays(-1), null, Ex("A"));
        var now = Run(DateTimeOffset.UtcNow, null, Ex("A"), done);
        await store.AddAsync(old, default); await store.AddAsync(now, default);
        var r = await n.NotifyAsync(now, true, default);
        Assert.False(r.Sent); Assert.Equal(0, r.NewExceptions); Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task ScheduledRunsOnly_IgnoresManualRuns_UnlessSetToAllRuns()
    {
        foreach (var (mode, expectMail) in new[] { ("ScheduledRuns", false), ("AllRuns", true) })
        {
            var (n, sender, _, store) = await Make(Settings(a => a.Notifications.NotifyOn = mode));
            await store.AddAsync(Run(DateTimeOffset.UtcNow.AddDays(-1), null), default);
            var now = Run(DateTimeOffset.UtcNow, null, Ex("N")); await store.AddAsync(now, default);
            var r = await n.NotifyAsync(now, scheduled: false, default);
            Assert.Equal(expectMail, r.Sent);
            Assert.Equal(expectMail ? 1 : 0, sender.Sent.Count);
        }
    }

    [Fact]
    public async Task AFailingMailServer_NeverThrows_AndIsRecorded_WithoutThePassword()
    {
        var (n, sender, state, store) = await Make(Settings());
        sender.Fail = new InvalidOperationException("550 relay denied");
        await store.AddAsync(Run(DateTimeOffset.UtcNow.AddDays(-1), null), default);
        var now = Run(DateTimeOffset.UtcNow, null, Ex("N")); await store.AddAsync(now, default);
        var r = await n.NotifyAsync(now, true, default);
        Assert.False(r.Sent); Assert.Contains("could not be sent", r.Message); Assert.Contains("relay denied", r.Message);
        Assert.False(state.Get().LastNoticeOk);
        Assert.DoesNotContain("smtp-secret", state.Get().LastNoticeMessage);
    }

    [Fact]
    public void TheSmtpPassword_IsProtectedAtRest_MaskedOnGet_AndKeptWhenBlank()
    {
        var s = Settings();
        Assert.DoesNotContain("smtp-secret", File.ReadAllText(Path.Combine(_dir, "settings.json")));
        var masked = SettingsService.Masked(s.Get());
        Assert.Equal("", masked.Notifications.Password); Assert.True(masked.Notifications.PasswordSet);
        var edit = s.Get(); edit.Notifications.Password = ""; edit.Notifications.SmtpHost = "smtp2.test"; s.Save(edit);
        Assert.Equal("smtp-secret", s.Get().Notifications.Password);                        // a blank field keeps the stored password
        var s2 = new SettingsService(_dir, new SecretProtector(new EphemeralDataProtectionProvider()), NullLogger<SettingsService>.Instance);
        Assert.Equal("smtp2.test", s2.Get().Notifications.SmtpHost);                        // and the rest survives a reload
    }

    [Fact]
    public void SettingsAreNormalised()
    {
        var s = Settings(a =>
        {
            a.Notifications.SmtpPort = 99999; a.Notifications.NotifyOn = "Sometimes"; a.Notifications.PublicUrl = "javascript:alert(1)";
            a.Notifications.Recipients = "ok@x.com, nonsense; OK@x.com\nsecond@y.com";
            a.Schedule.Enabled = true; a.Schedule.Time = "25:99"; a.Schedule.Days = new() { "monday", "FRI", "xyz", "Fri" };
        }).Get();
        Assert.Equal(587, s.Notifications.SmtpPort); Assert.Equal("ScheduledRuns", s.Notifications.NotifyOn); Assert.Equal("", s.Notifications.PublicUrl);
        Assert.Equal("ok@x.com, second@y.com", s.Notifications.Recipients);
        Assert.Equal("02:00", s.Schedule.Time); Assert.Equal(new[] { "Mon", "Fri" }, s.Schedule.Days);
    }

    // ---------------------------------------------------------------- schedule

    private sealed class FakeJob : IScheduledJob
    {
        public int Runs { get; private set; }
        public Task<ScheduledOutcome> RunAsync(CancellationToken ct) { Runs++; return Task.FromResult(new ScheduledOutcome(true, "ok", Guid.NewGuid())); }
    }

    [Fact]
    public async Task Schedule_RunsOnceADay_AtItsTime_OnTheChosenDays()
    {
        var clock = new DateTime(2026, 10, 14, 1, 0, 0);
        var s = Settings(a => { a.Schedule.Enabled = true; a.Schedule.Time = "02:00"; a.Schedule.Days = new() { "Mon", "Wed" }; });
        var job = new FakeJob(); var state = new AutomationState(null);
        var t = new ScheduleTicker(s, state, job, NullLogger.Instance, () => clock);

        Assert.False(await t.TickAsync(default));                         // Wed 01:00: not yet
        clock = new DateTime(2026, 10, 14, 2, 0, 0);
        Assert.True(await t.TickAsync(default)); Assert.Equal(1, job.Runs);
        clock = new DateTime(2026, 10, 14, 2, 30, 0);
        Assert.False(await t.TickAsync(default)); Assert.Equal(1, job.Runs); // same day: never twice
        clock = new DateTime(2026, 10, 15, 3, 0, 0);                      // Thu: not a chosen day
        Assert.False(await t.TickAsync(default));
        clock = new DateTime(2026, 10, 19, 2, 5, 0);                      // next Mon
        Assert.True(await t.TickAsync(default)); Assert.Equal(2, job.Runs);
    }

    [Fact]
    public async Task Schedule_Off_DoesNothing()
    {
        var s = Settings(a => { a.Schedule.Enabled = false; a.Schedule.Time = "00:01"; });
        var job = new FakeJob();
        var t = new ScheduleTicker(s, new AutomationState(null), job, NullLogger.Instance, () => new DateTime(2026, 10, 14, 12, 0, 0));
        Assert.False(await t.TickAsync(default)); Assert.Equal(0, job.Runs); Assert.Null(t.NextRun());
    }

    [Fact]
    public async Task AMissedRun_IsSkippedAndRecorded_NotStartedAtASurprisingHour()
    {
        var s = Settings(a => { a.Schedule.Enabled = true; a.Schedule.Time = "02:00"; });
        var job = new FakeJob(); var state = new AutomationState(null);
        var t = new ScheduleTicker(s, state, job, NullLogger.Instance, () => new DateTime(2026, 10, 14, 15, 0, 0));      // service started at 15:00
        Assert.False(await t.TickAsync(default));
        Assert.Equal(0, job.Runs);
        Assert.Contains("missed", state.Get().LastRunMessage); Assert.False(state.Get().LastRunOk);
        Assert.False(await t.TickAsync(default));                         // and the day is not retried
    }

    [Fact]
    public async Task ARestartDoesNotRepeatToday_AndACatchUpWithinSixHoursStillRuns()
    {
        var s = Settings(a => { a.Schedule.Enabled = true; a.Schedule.Time = "02:00"; });
        var file = new AutomationState(_dir);
        var job = new FakeJob();
        var late = new ScheduleTicker(s, file, job, NullLogger.Instance, () => new DateTime(2026, 10, 14, 4, 0, 0));       // service came up at 04:00
        Assert.True(await late.TickAsync(default)); Assert.Equal(1, job.Runs);

        var afterRestart = new ScheduleTicker(s, new AutomationState(_dir), job, NullLogger.Instance, () => new DateTime(2026, 10, 14, 4, 10, 0));
        Assert.False(await afterRestart.TickAsync(default)); Assert.Equal(1, job.Runs);
    }

    [Fact]
    public void NextRun_PointsAtTheNextChosenDay()
    {
        var s = Settings(a => { a.Schedule.Enabled = true; a.Schedule.Time = "02:00"; a.Schedule.Days = new() { "Fri" }; });
        var t = new ScheduleTicker(s, new AutomationState(null), new FakeJob(), NullLogger.Instance, () => new DateTime(2026, 10, 14, 9, 0, 0));       // Wed
        Assert.Equal(new DateTime(2026, 10, 16, 2, 0, 0), t.NextRun());
    }
}
