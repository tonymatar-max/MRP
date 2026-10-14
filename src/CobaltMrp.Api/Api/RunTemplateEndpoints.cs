using CobaltMrp.Api.Services;
using CobaltMrp.Domain;

namespace CobaltMrp.Api.Api;

public sealed record TemplateBody(string? Name, RunCriteria? Criteria);
public sealed record TemplateDto(Guid Id, string Name, string CreatedBy, DateTimeOffset UpdatedAt, RunCriteria Criteria);

/// <summary>Saved Run MRP option sets. Any signed-in user can save and use them; they never change SAP Business One.</summary>
public static class RunTemplateEndpoints
{
    public static void MapRunTemplates(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/run-templates");

        g.MapGet("", async (IRunTemplateStore store, CancellationToken ct) =>
            (await store.ListAsync(ct)).Select(ToDto).Where(d => d is not null));

        g.MapPost("", async (TemplateBody body, HttpContext ctx, IRunTemplateStore store, CancellationToken ct) =>
        {
            var (name, criteria, error) = Check(body, isUpdate: false);
            if (error is not null) return Results.BadRequest(new { message = error });
            if (await NameTaken(store, name!, null, ct)) return Results.Conflict(new { message = "A template with this name already exists." });
            var t = new RunTemplate { Name = name!, CriteriaJson = criteria!.ToJson(), CreatedBy = ctx.User.Identity?.Name ?? "" };
            await store.SaveAsync(t, ct);
            return Results.Ok(ToDto(t));
        });

        g.MapPut("/{id:guid}", async (Guid id, TemplateBody body, IRunTemplateStore store, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } t) return Results.NotFound(new { message = "Template not found." });
            var (name, criteria, error) = Check(body, isUpdate: true);
            if (error is not null) return Results.BadRequest(new { message = error });
            if (name is not null)
            {
                if (await NameTaken(store, name, id, ct)) return Results.Conflict(new { message = "A template with this name already exists." });
                t.Name = name;
            }
            if (criteria is not null) t.CriteriaJson = criteria.ToJson();
            t.UpdatedAt = DateTimeOffset.UtcNow;
            await store.SaveAsync(t, ct);
            return Results.Ok(ToDto(t));
        });

        g.MapDelete("/{id:guid}", async (Guid id, IRunTemplateStore store, CancellationToken ct) =>
            await store.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound(new { message = "Template not found." }));
    }

    internal static TemplateDto? ToDto(RunTemplate t) =>
        RunCriteria.FromJson(t.CriteriaJson) is { } c ? new TemplateDto(t.Id, t.Name, t.CreatedBy, t.UpdatedAt, c) : null;

    private static (string? Name, RunCriteria? Criteria, string? Error) Check(TemplateBody b, bool isUpdate)
    {
        string? name = null;
        if (b.Name is not null)
        {
            name = b.Name.Trim();
            if (name.Length == 0 || name.Length > 80) return (null, null, "Give the template a name of 1 to 80 characters.");
        }
        else if (!isUpdate) return (null, null, "Give the template a name.");
        RunCriteria? criteria = null;
        if (b.Criteria is not null)
        {
            criteria = b.Criteria;
            criteria.Horizon.StartDate = null;                       // a template always plans from today
            if (criteria.Validate() is { } bad) return (null, null, bad);
        }
        else if (!isUpdate) return (null, null, "The template has no options.");
        return (name, criteria, null);
    }

    private static async Task<bool> NameTaken(IRunTemplateStore store, string name, Guid? except, CancellationToken ct) =>
        (await store.ListAsync(ct)).Any(t => t.Id != except && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}
