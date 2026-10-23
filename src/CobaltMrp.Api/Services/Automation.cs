using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using CobaltMrp.Api.Settings;
using CobaltMrp.Domain;
using CobaltMrp.Engine;

namespace CobaltMrp.Api.Services;

// ---- Email --------------------------------------------------------------------------------------------------

public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string Text, string Html);

/// <summary>Sends one email. A seam so tests do not need a mail server.</summary>
public interface IEmailSender
{
    Task SendAsync(NotificationSettings settings, EmailMessage message, CancellationToken ct);
}

public sealed class SmtpEmailSender : IEmailSender
{
    public async Task SendAsync(NotificationSettings s, EmailMessage m, CancellationToken ct)
    {
        using var client = new SmtpClient(s.SmtpHost, s.SmtpPort) { EnableSsl = s.UseSsl, Timeout = 30_000, DeliveryMethod = SmtpDeliveryMethod.Network };
        if (!string.IsNullOrWhiteSpace(s.UserName)) client.Credentials = new NetworkCredential(s.UserName, s.Password);
        using var mail = new MailMessage { From = new MailAddress(s.From), Subject = m.Subject, SubjectEncoding = Encoding.UTF8, BodyEncoding = Encoding.UTF8 };
        foreach (var to in m.To) mail.To.Add(to);
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(m.Text, Encoding.UTF8, "text/plain"));
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(m.Html, Encoding.UTF8, "text/html"));
        await client.SendMailAsync(mail, ct);
    }

    /// <summary>A readable one-line reason for a failed send (never includes the password).</summary>
    public static string Explain(Exception ex) => ex switch
    {
        SmtpException { InnerException: { } inner } => $"{ex.Message} ({inner.Message})",
        OperationCanceledException => "The mail server did not answer in time.",
        _ => ex.Message,
    };
}

// ---- What the automation did last (kept across restarts) -----------------------------------------------------

/// <summary>What one schedule did last. Lives in automation-state.json, never in settings.</summary>
public sealed class ScheduleStatus
{
    /// <summary>The local date (yyyy-MM-dd) this schedule last handled, so a restart never repeats a day.</summary>
    public string? LastScheduledDate { get; set; }
    public DateTimeOffset? LastFiredAt { get; set; }
    public bool? LastOk { get; set; }
    public string? LastMessage { get; set; }
    public Guid? LastRunId { get; set; }
}

public sealed class AutomationInfo
{
    /// <summary>Per schedule id.</summary>
    public Dictionary<string, ScheduleStatus> Schedules { get; set; } = new();
    public DateTimeOffset? LastNoticeAt { get; set; }
    public bool? LastNoticeOk { get; set; }
    public string? LastNoticeMessage { get; set; }
    public int LastNoticeNewExceptions { get; set; }

    // Legacy single-schedule fields (before multiple schedules). Read once and moved into Schedules["default"].
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? LastRunAt { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public Guid? LastRunId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public bool? LastRunOk { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? LastRunMessage { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? LastScheduledDate { get; set; }
}

public sealed class AutomationState
{
    private readonly string? _path;
    private readonly object _lock = new();
    private AutomationInfo _info = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <param name="dataDir">Folder for <c>automation-state.json</c>; null keeps it in memory only (tests).</param>
    public AutomationState(string? dataDir)
    {
        if (dataDir is null) return;
        _path = Path.Combine(dataDir, "automation-state.json");
        try { if (File.Exists(_path)) _info = JsonSerializer.Deserialize<AutomationInfo>(File.ReadAllText(_path), Json) ?? new(); }
        catch { _info = new(); }
        _info.Schedules ??= new();
        MigrateLegacy(_info);
    }

    /// <summary>The old single schedule's last state becomes the state of the migrated schedule (id "default"): no run repeats or is lost.</summary>
    internal static void MigrateLegacy(AutomationInfo i)
    {
        if (i.LastScheduledDate is null && i.LastRunAt is null) return;
        if (!i.Schedules.ContainsKey(ScheduleSettings.MigratedId))
            i.Schedules[ScheduleSettings.MigratedId] = new ScheduleStatus
            {
                LastScheduledDate = i.LastScheduledDate, LastFiredAt = i.LastRunAt, LastOk = i.LastRunOk, LastMessage = i.LastRunMessage, LastRunId = i.LastRunId,
            };
        i.LastScheduledDate = null; i.LastRunAt = null; i.LastRunOk = null; i.LastRunMessage = null; i.LastRunId = null;
    }

    public AutomationInfo Get() { lock (_lock) return JsonSerializer.Deserialize<AutomationInfo>(JsonSerializer.Serialize(_info, Json), Json)!; }

    /// <summary>Change one schedule's status (created when missing).</summary>
    public void UpdateSchedule(string id, Action<ScheduleStatus> change) =>
        Update(i => { if (!i.Schedules.TryGetValue(id, out var st)) i.Schedules[id] = st = new ScheduleStatus(); change(st); });

    public void Update(Action<AutomationInfo> change)
    {
        lock (_lock)
        {
            change(_info);
            if (_path is null) return;
            try { var tmp = _path + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(_info, Json)); File.Move(tmp, _path, overwrite: true); }
            catch { /* the state is a convenience: never fail a run because it could not be saved */ }
        }
    }
}

// ---- Launching a run (shared by the Run MRP button and the schedule) -----------------------------------------

public sealed class RunLauncher(RunPlanner planner, MrpEngine engine, IRunStore store)
{
    public async Task<(MrpRun? Run, string? Error)> LaunchAsync(RunCriteria criteria, string user, string? name, string? notes, CancellationToken ct)
    {
        var (prep, error) = await planner.PrepareAsync(criteria, user, ct);
        if (prep is null) return (null, error);

        MrpRun run;
        try { run = engine.Run(prep.Data, prep.Options); }
        // e.g. a BOM that contains itself: tell the planner what is wrong instead of a bare 500
        catch (InvalidOperationException e) { return (null, e.Message); }
        run.Notes = notes;
        run.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(name.Trim().Length, 100)];
        run.CriteriaJson = prep.Criteria.ToJson();
        await store.AddAsync(run, ct);
        return (run, null);
    }
}

// ---- New-exception alerts ------------------------------------------------------------------------------------

public sealed record NotifyResult(bool Sent, int NewExceptions, string Message);

/// <summary>
/// After a run, emails the exceptions the run before it did not have ("new" = a different type, item, warehouse or source
/// document line). Never throws: a failed email must not fail a run, it is recorded and shown in Settings.
/// </summary>
public sealed class ExceptionNotifier(SettingsService settings, IEmailSender sender, AutomationState state, IRunStore store, ILogger<ExceptionNotifier> log)
{
    public static string Key(ExceptionMessage e) => string.Join('|', e.Type, e.ItemCode, e.WarehouseCode ?? "", e.SourceDocKey ?? "", e.SourceLine?.ToString() ?? "");

    /// <param name="sameTemplateAs">For scheduled runs: the template name, so the comparison is with the previous scheduled run OF THAT
    /// TEMPLATE (name "&lt;template&gt; · date", started by the scheduler). Null = the run just before this one, whatever it was.</param>
    public async Task<NotifyResult> NotifyAsync(MrpRun run, bool scheduled, CancellationToken ct, string? sameTemplateAs = null)
    {
        var n = settings.Get().Notifications;
        if (!n.IsConfigured) return new(false, 0, "Notifications are off or not fully set up.");
        if (!scheduled && n.NotifyOn != "AllRuns") return new(false, 0, "Only scheduled runs send an email (Settings > Automation).");
        try
        {
            var previous = await PreviousAsync(run, sameTemplateAs, ct);
            var seen = previous?.Exceptions.Select(Key).ToHashSet() ?? new HashSet<string>();
            var fresh = run.Exceptions.Where(e => e.ActionStatus != "Done" && (previous is null || !seen.Contains(Key(e)))).ToList();
            if (previous is null) return Record(false, 0, "This is the first run: nothing to compare with.");
            if (fresh.Count == 0) return Record(false, 0, "No new exceptions: no email sent.");

            // Each recipient gets only the areas they asked for; people with the same selection share one email.
            var types = run.PlannedOrders.ToDictionary(p => p.Id, p => p.Type);
            var areaOf = fresh.ToDictionary(e => e.ExceptionId, e => ExceptionAreas.Of(e, types));
            var groups = n.RecipientRules()
                .Select(r => (Rule: r, Items: r.Areas is null ? fresh : fresh.Where(e => r.Areas.Contains(areaOf[e.ExceptionId])).ToList()))
                .Where(x => x.Items.Count > 0)
                .GroupBy(x => string.Join(",", x.Items.Select(e => e.ExceptionId)));
            int emails = 0, people = 0;
            foreach (var g in groups)
            {
                var items = g.First().Items;
                var to = g.Select(x => x.Rule.Address).ToList();
                await sender.SendAsync(n, Build(run, items, n.PublicUrl, previous) with { To = to }, ct);
                emails++; people += to.Count;
            }
            if (emails == 0) return Record(false, fresh.Count, $"{fresh.Count} new exception(s), but none match any recipient's areas: no email sent.");
            return Record(true, fresh.Count, $"Emailed new exceptions to {people} recipient(s).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not send the new-exceptions email");
            return Record(false, 0, "The email could not be sent: " + SmtpEmailSender.Explain(ex), failed: true);
        }
    }

    private NotifyResult Record(bool sent, int count, string message, bool failed = false)
    {
        state.Update(i => { i.LastNoticeAt = DateTimeOffset.UtcNow; i.LastNoticeOk = !failed; i.LastNoticeMessage = message; i.LastNoticeNewExceptions = count; });
        return new NotifyResult(sent, count, message);
    }

    private async Task<MrpRun?> PreviousAsync(MrpRun run, string? sameTemplateAs, CancellationToken ct)
    {
        var list = await store.ListAsync(ct);
        var idx = list.ToList().FindIndex(r => r.Id == run.Id);
        IEnumerable<RunSummary> older = idx >= 0 ? list.Skip(idx + 1) : list.Where(r => r.Id != run.Id);
        if (sameTemplateAs is not null)
        {
            var prefix = sameTemplateAs + ScheduledJob.NameSeparator;
            older = older.Where(r => r.RunBy == ScheduledJob.User && r.Name != null && r.Name.StartsWith(prefix, StringComparison.Ordinal));
        }
        var prev = older.FirstOrDefault();
        return prev is null ? null : await store.GetAsync(prev.Id, ct);
    }

    internal static EmailMessage Build(MrpRun run, IReadOnlyList<ExceptionMessage> fresh, string publicUrl, MrpRun? previous)
    {
        var label = string.IsNullOrWhiteSpace(run.Name) ? run.RunAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : $"{run.Name} ({run.RunAt.ToLocalTime():yyyy-MM-dd HH:mm})";
        var byType = fresh.GroupBy(e => e.Type).OrderByDescending(g => g.Count()).ToList();
        var link = string.IsNullOrEmpty(publicUrl) ? null : publicUrl + "/exceptions";
        const int Max = 25;
        var shown = fresh.OrderBy(e => e.Type).ThenBy(e => e.ItemCode).Take(Max).ToList();

        var text = new StringBuilder();
        text.AppendLine($"Cobalt MRP run {label} has {fresh.Count} new exception(s) compared with the run before it.");
        text.AppendLine(string.Join(", ", byType.Select(g => $"{g.Key}: {g.Count()}")));
        text.AppendLine();
        foreach (var e in shown) text.AppendLine($"- [{e.Type}] {e.ItemCode}{(e.WarehouseCode is null ? "" : " / " + e.WarehouseCode)}: {e.Message}");
        if (fresh.Count > Max) text.AppendLine($"... and {fresh.Count - Max} more.");
        if (link is not null) { text.AppendLine(); text.AppendLine("Open the Exceptions page: " + link); }

        var html = new StringBuilder("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1b1f2e\">");
        html.Append($"<p><b>Cobalt MRP</b> run <b>{WebUtility.HtmlEncode(label)}</b> has <b>{fresh.Count}</b> new exception(s) compared with the run before it.</p>");
        html.Append("<p>" + string.Join(" &middot; ", byType.Select(g => $"{WebUtility.HtmlEncode(g.Key.ToString())}: {g.Count()}")) + "</p>");
        html.Append("<table cellpadding=\"6\" style=\"border-collapse:collapse;border:1px solid #e3e6f0\"><tr style=\"background:#ebf1fc\"><th align=\"left\">Type</th><th align=\"left\">Item</th><th align=\"left\">Warehouse</th><th align=\"left\">Message</th></tr>");
        foreach (var e in shown)
            html.Append($"<tr style=\"border-top:1px solid #e3e6f0\"><td>{WebUtility.HtmlEncode(e.Type.ToString())}</td><td>{WebUtility.HtmlEncode(e.ItemCode)}</td><td>{WebUtility.HtmlEncode(e.WarehouseCode ?? "")}</td><td>{WebUtility.HtmlEncode(e.Message)}</td></tr>");
        html.Append("</table>");
        if (fresh.Count > Max) html.Append($"<p>... and {fresh.Count - Max} more.</p>");
        if (link is not null) html.Append($"<p><a href=\"{WebUtility.HtmlEncode(link)}\">Open the Exceptions page</a></p>");
        html.Append("</div>");

        return new EmailMessage(Array.Empty<string>(), $"Cobalt MRP: {fresh.Count} new exception(s) ({label})", text.ToString(), html.ToString());
    }
}

// ---- The schedules ------------------------------------------------------------------------------------------

public sealed record ScheduledOutcome(bool Ok, string Message, Guid? RunId, bool Busy = false);

/// <summary>The work one schedule does: run MRP with its template, then send the new-exceptions email.</summary>
public interface IScheduledJob
{
    Task<ScheduledOutcome> RunAsync(ScheduleSettings schedule, CancellationToken ct);
}

/// <summary>
/// Concurrency rule for scheduled runs. The SAME schedule never overlaps itself (a second attempt is refused at once);
/// runs of DIFFERENT schedules are serialised (one MRP run at a time), so the engine, the run store and the SMTP step are never
/// used by two scheduled runs at once.
/// </summary>
public sealed class ScheduleGate
{
    private readonly SemaphoreSlim _all = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _active = new();

    public bool IsActive(string id) => _active.ContainsKey(id);

    public async Task<(bool Entered, T? Value)> TryRunAsync<T>(string id, Func<Task<T>> work, CancellationToken ct)
    {
        if (!_active.TryAdd(id, 0)) return (false, default);
        try
        {
            await _all.WaitAsync(ct);
            try { return (true, await work()); }
            finally { _all.Release(); }
        }
        finally { _active.TryRemove(id, out _); }
    }
}

public sealed class ScheduledJob(IServiceScopeFactory scopes, AutomationState state, ScheduleGate gate, ILogger<ScheduledJob> log) : IScheduledJob
{
    /// <summary>Who started the run (shown as "Run by").</summary>
    public const string User = "scheduler";
    /// <summary>Run name = template name + this + local "yyyy-MM-dd HH:mm".</summary>
    public const string NameSeparator = " · ";

    public static string RunName(string templateName, DateTime local) => templateName + NameSeparator + local.ToString("yyyy-MM-dd HH:mm");

    public async Task<ScheduledOutcome> RunAsync(ScheduleSettings schedule, CancellationToken ct)
    {
        var (entered, outcome) = await gate.TryRunAsync(schedule.Id, () => ExecuteAsync(schedule, ct), ct);
        return entered ? outcome! : new ScheduledOutcome(false, "This schedule is already running.", null, Busy: true);
    }

    private async Task<ScheduledOutcome> ExecuteAsync(ScheduleSettings schedule, CancellationToken ct)
    {
        var label = string.IsNullOrWhiteSpace(schedule.Name) ? "Schedule " + schedule.Id[..Math.Min(8, schedule.Id.Length)] : schedule.Name;
        ScheduledOutcome Done(bool ok, string message, Guid? runId)
        {
            state.UpdateSchedule(schedule.Id, st => { st.LastFiredAt = DateTimeOffset.UtcNow; st.LastOk = ok; st.LastMessage = message; st.LastRunId = runId; });
            if (ok) log.LogInformation("Schedule '{Schedule}': {Message}", label, message); else log.LogWarning("Schedule '{Schedule}': {Message}", label, message);
            return new ScheduledOutcome(ok, message, runId);
        }

        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            string templateName; RunCriteria criteria;
            if (Guid.TryParse(schedule.TemplateId, out var tid))
            {
                if (await sp.GetRequiredService<IRunTemplateStore>().GetAsync(tid, ct) is not { } t)
                    return Done(false, "Skipped: the template no longer exists. Choose another template for this schedule.", null);
                if (RunCriteria.FromJson(t.CriteriaJson) is not { } tc)
                    return Done(false, $"Skipped: the options saved in template \"{t.Name}\" cannot be read. Save the template again.", null);
                criteria = tc; templateName = t.Name;
                if (criteria.Validate() is { } bad)
                    return Done(false, $"Skipped: the options in template \"{t.Name}\" are no longer valid ({bad}).", null);
            }
            else if (string.IsNullOrWhiteSpace(schedule.TemplateId) && schedule.Enabled)
            {   // a schedule saved before templates were required: keeps using the Settings defaults
                criteria = sp.GetRequiredService<RunPlanner>().Defaults(null); templateName = "Settings defaults";
            }
            else return Done(false, "Skipped: no template is chosen for this schedule.", null);
            criteria.Horizon.StartDate = null;          // always plan from today

            var launcher = sp.GetRequiredService<RunLauncher>();
            var (run, error) = await launcher.LaunchAsync(criteria, User, RunName(templateName, DateTime.Now), $"Started by schedule \"{label}\"", ct);
            if (run is null) return Done(false, "The scheduled run failed: " + error, null);

            var notice = await sp.GetRequiredService<ExceptionNotifier>().NotifyAsync(run, scheduled: true, ct, sameTemplateAs: templateName);
            return Done(true, $"Run finished: {run.PlannedOrders.Count} planned orders, {run.Exceptions.Count} exceptions. {notice.Message}", run.Id);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "Schedule '{Schedule}' failed", label);
            state.UpdateSchedule(schedule.Id, st => { st.LastFiredAt = DateTimeOffset.UtcNow; st.LastOk = false; st.LastMessage = "The scheduled run failed: " + ex.Message; st.LastRunId = null; });
            return new ScheduledOutcome(false, "The scheduled run failed: " + ex.Message, null);
        }
    }
}

/// <summary>
/// Decides, once a tick, which schedules are due. Pure logic over a clock so it can be tested. Each schedule fires at most once
/// per local day, at or after its time, on its chosen days; one loop evaluates them all, one after the other.
/// </summary>
public sealed class ScheduleTicker(SettingsService settings, AutomationState state, IScheduledJob job, ILogger log, Func<DateTime>? clock = null)
{
    /// <summary>A run missed by more than this (service stopped at the scheduled time) is skipped, not started at a surprising hour.</summary>
    public static readonly TimeSpan CatchUp = TimeSpan.FromHours(6);
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.Now);

    public static string Day(DateTime d) => d.DayOfWeek switch
    {
        DayOfWeek.Monday => "Mon", DayOfWeek.Tuesday => "Tue", DayOfWeek.Wednesday => "Wed", DayOfWeek.Thursday => "Thu",
        DayOfWeek.Friday => "Fri", DayOfWeek.Saturday => "Sat", _ => "Sun",
    };

    /// <returns>True when at least one run was started by this tick.</returns>
    public async Task<bool> TickAsync(CancellationToken ct)
    {
        var schedules = settings.Get().Schedules ?? new();
        var now = _now();
        var started = false;
        foreach (var sch in schedules)
        {
            ct.ThrowIfCancellationRequested();
            try { started |= await TickOneAsync(sch, now, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.LogError(ex, "Schedule {Id} could not be evaluated", sch.Id); }
        }
        // Forget the state of schedules that were removed.
        var ids = schedules.Select(s => s.Id).ToHashSet();
        if (state.Get().Schedules.Keys.Any(k => !ids.Contains(k)))
            state.Update(i => { foreach (var k in i.Schedules.Keys.Where(k => !ids.Contains(k)).ToList()) i.Schedules.Remove(k); });
        return started;
    }

    private async Task<bool> TickOneAsync(ScheduleSettings sch, DateTime now, CancellationToken ct)
    {
        if (!sch.Enabled || !TimeOnly.TryParseExact(sch.Time, "HH:mm", out var at)) return false;
        if (!sch.Days.Contains(Day(now))) return false;
        var due = now.Date + at.ToTimeSpan();
        if (now < due) return false;
        var today = now.ToString("yyyy-MM-dd");
        if (state.Get().Schedules.GetValueOrDefault(sch.Id)?.LastScheduledDate == today) return false;

        state.UpdateSchedule(sch.Id, st => st.LastScheduledDate = today);          // claim the day first: a crash must not repeat it
        if (now - due > CatchUp)
        {
            var msg = $"The scheduled run for {at:HH\\:mm} was missed (the service was not running) and was skipped.";
            state.UpdateSchedule(sch.Id, st => { st.LastFiredAt = DateTimeOffset.UtcNow; st.LastOk = false; st.LastMessage = msg; st.LastRunId = null; });
            log.LogWarning("Schedule {Id}: {Message}", sch.Id, msg);
            return false;
        }
        await job.RunAsync(sch, ct);
        return true;
    }

    /// <summary>The next time this schedule will fire (server local time), or null when it is off.</summary>
    public DateTime? NextRun(ScheduleSettings sch, AutomationInfo info)
    {
        if (!sch.Enabled || sch.Days.Count == 0 || !TimeOnly.TryParseExact(sch.Time, "HH:mm", out var at)) return null;
        var now = _now();
        var handled = info.Schedules.GetValueOrDefault(sch.Id)?.LastScheduledDate;
        for (var d = 0; d <= 7; d++)
        {
            var day = now.Date.AddDays(d);
            if (!sch.Days.Contains(Day(day)) || handled == day.ToString("yyyy-MM-dd")) continue;
            var t = day + at.ToTimeSpan();
            if (t <= now && now - t > CatchUp) continue;      // missed by too much: the next tick skips it
            return t;                                          // in the past but within the catch-up window = due on the next tick
        }
        return null;
    }
}

public sealed class ScheduledRunService(ScheduleTicker ticker, ILogger<ScheduledRunService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the host a moment to finish starting, then look every 30 seconds.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ticker.TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogError(ex, "The schedule check failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
