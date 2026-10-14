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

public sealed class AutomationInfo
{
    public DateTimeOffset? LastRunAt { get; set; }
    public Guid? LastRunId { get; set; }
    public bool? LastRunOk { get; set; }
    public string? LastRunMessage { get; set; }
    /// <summary>The local date (yyyy-MM-dd) the schedule last handled, so a restart never repeats a day.</summary>
    public string? LastScheduledDate { get; set; }
    public DateTimeOffset? LastNoticeAt { get; set; }
    public bool? LastNoticeOk { get; set; }
    public string? LastNoticeMessage { get; set; }
    public int LastNoticeNewExceptions { get; set; }
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
    }

    public AutomationInfo Get() { lock (_lock) return JsonSerializer.Deserialize<AutomationInfo>(JsonSerializer.Serialize(_info, Json), Json)!; }

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

    public async Task<NotifyResult> NotifyAsync(MrpRun run, bool scheduled, CancellationToken ct)
    {
        var n = settings.Get().Notifications;
        if (!n.IsConfigured) return new(false, 0, "Notifications are off or not fully set up.");
        if (!scheduled && n.NotifyOn != "AllRuns") return new(false, 0, "Only scheduled runs send an email (Settings > Automation).");
        try
        {
            var previous = await PreviousAsync(run, ct);
            var seen = previous?.Exceptions.Select(Key).ToHashSet() ?? new HashSet<string>();
            var fresh = run.Exceptions.Where(e => e.ActionStatus != "Done" && (previous is null || !seen.Contains(Key(e)))).ToList();
            if (previous is null) return Record(false, 0, "This is the first run: nothing to compare with.");
            if (fresh.Count == 0) return Record(false, 0, "No new exceptions: no email sent.");

            var msg = Build(run, fresh, n.PublicUrl, previous) with { To = n.RecipientList() };
            await sender.SendAsync(n, msg, ct);
            return Record(true, fresh.Count, $"Emailed {fresh.Count} new exception(s) to {n.RecipientList().Count} recipient(s).");
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

    private async Task<MrpRun?> PreviousAsync(MrpRun run, CancellationToken ct)
    {
        var list = await store.ListAsync(ct);
        var idx = list.ToList().FindIndex(r => r.Id == run.Id);
        var prev = idx >= 0 ? list.Skip(idx + 1).FirstOrDefault() : list.FirstOrDefault(r => r.Id != run.Id);
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

// ---- The schedule --------------------------------------------------------------------------------------------

public sealed record ScheduledOutcome(bool Ok, string Message, Guid? RunId);

/// <summary>The work the schedule does: run MRP, then send the new-exceptions email.</summary>
public interface IScheduledJob
{
    Task<ScheduledOutcome> RunAsync(CancellationToken ct);
}

public sealed class ScheduledJob(IServiceScopeFactory scopes, SettingsService settings, AutomationState state, ILogger<ScheduledJob> log) : IScheduledJob
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public async Task<ScheduledOutcome> RunAsync(CancellationToken ct)
    {
        if (!await OneAtATime.WaitAsync(0, ct)) return new(false, "A scheduled run is already in progress.", null);
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var s = settings.Get();
            var planner = sp.GetRequiredService<RunPlanner>();
            string? templateName = null, templateNote = null;
            RunCriteria criteria;
            if (Guid.TryParse(s.Schedule.TemplateId, out var tid) && await sp.GetRequiredService<IRunTemplateStore>().GetAsync(tid, ct) is { } t
                && RunCriteria.FromJson(t.CriteriaJson) is { } tc)
            { criteria = tc; templateName = t.Name; }
            else
            {
                criteria = planner.Defaults(null);
                if (!string.IsNullOrWhiteSpace(s.Schedule.TemplateId)) templateNote = " (the chosen template no longer exists: the Settings defaults were used)";
            }
            criteria.Horizon.StartDate = null;          // always plan from today

            var launcher = sp.GetRequiredService<RunLauncher>();
            var name = $"Scheduled: {templateName ?? "Settings defaults"} ({DateTime.Now:yyyy-MM-dd})";
            var (run, error) = await launcher.LaunchAsync(criteria, "scheduler", name, "Started by the schedule" + templateNote, ct);
            if (run is null)
            {
                var msg = "The scheduled run failed: " + error;
                state.Update(i => { i.LastRunAt = DateTimeOffset.UtcNow; i.LastRunId = null; i.LastRunOk = false; i.LastRunMessage = msg; });
                log.LogWarning("{Message}", msg);
                return new(false, msg, null);
            }

            var notice = await sp.GetRequiredService<ExceptionNotifier>().NotifyAsync(run, scheduled: true, ct);
            var ok = $"Run finished: {run.PlannedOrders.Count} planned orders, {run.Exceptions.Count} exceptions. {notice.Message}{templateNote}";
            state.Update(i => { i.LastRunAt = DateTimeOffset.UtcNow; i.LastRunId = run.Id; i.LastRunOk = true; i.LastRunMessage = ok; });
            log.LogInformation("Scheduled run {Id}: {Message}", run.Id, ok);
            return new(true, ok, run.Id);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var msg = "The scheduled run failed: " + ex.Message;
            state.Update(i => { i.LastRunAt = DateTimeOffset.UtcNow; i.LastRunOk = false; i.LastRunMessage = msg; });
            log.LogError(ex, "Scheduled run failed");
            return new(false, msg, null);
        }
        finally { OneAtATime.Release(); }
    }
}

/// <summary>Decides, once a tick, whether the schedule is due. Pure logic over a clock so it can be tested.</summary>
public sealed class ScheduleTicker(SettingsService settings, AutomationState state, IScheduledJob job, ILogger log, Func<DateTime>? clock = null)
{
    /// <summary>A run missed by more than this (service stopped at the scheduled time) is skipped, not started at a surprising hour.</summary>
    public static readonly TimeSpan CatchUp = TimeSpan.FromHours(6);
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.Now);

    private static string Day(DateTime d) => d.DayOfWeek switch
    {
        DayOfWeek.Monday => "Mon", DayOfWeek.Tuesday => "Tue", DayOfWeek.Wednesday => "Wed", DayOfWeek.Thursday => "Thu",
        DayOfWeek.Friday => "Fri", DayOfWeek.Saturday => "Sat", _ => "Sun",
    };

    /// <returns>True when a run was started by this tick.</returns>
    public async Task<bool> TickAsync(CancellationToken ct)
    {
        var sch = settings.Get().Schedule;
        if (!sch.Enabled || !TimeOnly.TryParseExact(sch.Time, "HH:mm", out var at)) return false;
        var now = _now();
        if (!sch.Days.Contains(Day(now))) return false;
        var due = now.Date + at.ToTimeSpan();
        if (now < due) return false;
        var today = now.ToString("yyyy-MM-dd");
        if (state.Get().LastScheduledDate == today) return false;

        state.Update(i => i.LastScheduledDate = today);          // claim the day first: a crash must not repeat it
        if (now - due > CatchUp)
        {
            var msg = $"The scheduled run for {at:HH\\:mm} was missed (the service was not running) and was skipped.";
            state.Update(i => { i.LastRunAt = DateTimeOffset.UtcNow; i.LastRunOk = false; i.LastRunMessage = msg; });
            log.LogWarning("{Message}", msg);
            return false;
        }
        await job.RunAsync(ct);
        return true;
    }

    /// <summary>The next time the schedule will fire (server local time), or null when it is off.</summary>
    public DateTime? NextRun()
    {
        var sch = settings.Get().Schedule;
        if (!sch.Enabled || sch.Days.Count == 0 || !TimeOnly.TryParseExact(sch.Time, "HH:mm", out var at)) return null;
        var now = _now();
        var handled = state.Get().LastScheduledDate;
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
