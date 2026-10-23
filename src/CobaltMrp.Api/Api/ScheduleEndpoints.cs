using CobaltMrp.Api.Services;
using CobaltMrp.Api.Settings;

namespace CobaltMrp.Api.Api;

public sealed record ScheduleStatusDto(DateTimeOffset? LastFiredAt, bool? LastOk, string? LastMessage, Guid? LastRunId, DateTime? NextDueAt);
public sealed record ScheduleDto(string Id, string Name, bool Enabled, string TemplateId, string? TemplateName, bool TemplateMissing,
    string Time, List<string> Days, bool Running, ScheduleStatusDto Status);

/// <summary>
/// The scheduled runs and what each did last. The schedules themselves are edited with the rest of the settings
/// (<c>PUT /api/settings</c>); the status lives in automation-state.json. Administrators only, like all automation settings.
/// </summary>
public static class ScheduleEndpoints
{
    public static void MapSchedules(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/schedules").RequireAuthorization("Admin");

        g.MapGet("", async (SettingsService settings, AutomationState state, ScheduleTicker ticker, ScheduleGate gate, IRunTemplateStore templates, CancellationToken ct) =>
        {
            var names = (await templates.ListAsync(ct)).ToDictionary(t => t.Id.ToString(), t => t.Name);
            var info = state.Get();
            return Results.Ok((settings.Get().Schedules ?? new()).Select(s =>
            {
                var st = info.Schedules.GetValueOrDefault(s.Id);
                names.TryGetValue(s.TemplateId, out var tn);
                // an empty template id on an enabled schedule is the older "Settings defaults" behaviour, not a missing template
                var missing = !string.IsNullOrWhiteSpace(s.TemplateId) && tn is null;
                return new ScheduleDto(s.Id, s.Name, s.Enabled, s.TemplateId, tn, missing, s.Time, s.Days, gate.IsActive(s.Id),
                    new ScheduleStatusDto(st?.LastFiredAt, st?.LastOk, st?.LastMessage, st?.LastRunId, ticker.NextRun(s, info)));
            }).ToList());
        });

        // Fires one schedule right now with the template as saved. It does not count as the schedule's run of the day.
        g.MapPost("/{id}/run-now", async (string id, SettingsService settings, IScheduledJob job, CancellationToken ct) =>
        {
            if ((settings.Get().Schedules ?? new()).FirstOrDefault(s => s.Id == id) is not { } schedule)
                return Results.NotFound(new { message = "Schedule not found (save the settings first)." });
            var outcome = await job.RunAsync(schedule, ct);
            return outcome.Busy ? Results.Json(outcome, statusCode: StatusCodes.Status409Conflict) : Results.Ok(outcome);
        });
    }
}
