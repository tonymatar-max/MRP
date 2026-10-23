using CobaltMrp.Api.Services;
using CobaltMrp.Api.Settings;

namespace CobaltMrp.Api.Api;

/// <summary>New-exception emails: status and a test email. Administrators only. (Schedules: see ScheduleEndpoints.)</summary>
public static class AutomationEndpoints
{
    public static void MapAutomation(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/automation").RequireAuthorization("Admin");

        // Notification status only: the schedules and their status are under /api/schedules.
        g.MapGet("/status", (SettingsService settings, AutomationState state) =>
        {
            var i = state.Get();
            return Results.Ok(new
            {
                notificationsConfigured = settings.Get().Notifications.IsConfigured,
                lastNotice = new { at = i.LastNoticeAt, ok = i.LastNoticeOk, message = i.LastNoticeMessage, newExceptions = i.LastNoticeNewExceptions },
            });
        });

        api.MapPost("/settings/test/email", async (NotificationSettings? body, SettingsService settings, IEmailSender sender, CancellationToken ct) =>
        {
            var cur = settings.Get();
            if (body is not null) cur.Notifications = body;
            var n = settings.Preview(cur).Notifications;      // a blank password keeps the stored one
            if (string.IsNullOrWhiteSpace(n.SmtpHost)) return Results.Ok(new TestResult(false, "Enter the SMTP server."));
            if (string.IsNullOrWhiteSpace(n.From)) return Results.Ok(new TestResult(false, "Enter the From address."));
            var to = n.RecipientList();
            if (to.Count == 0) return Results.Ok(new TestResult(false, "Enter at least one recipient email address."));
            try
            {
                await sender.SendAsync(n, new EmailMessage(to, "Cobalt MRP test email",
                    "This is a test email from Cobalt MRP. If you can read it, new-exception alerts will reach you.",
                    "<p>This is a test email from <b>Cobalt MRP</b>. If you can read it, new-exception alerts will reach you.</p>"), ct);
                return Results.Ok(new TestResult(true, $"Test email sent to {to.Count} recipient(s)."));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return Results.Ok(new TestResult(false, "Could not send: " + SmtpEmailSender.Explain(ex))); }
        }).RequireAuthorization("Admin");
    }
}
