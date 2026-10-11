using CobaltMrp.Api.Services;
using CobaltMrp.Api.Settings;
using CobaltMrp.Data;
using CobaltMrp.Data.ServiceLayer;
using CobaltMrp.Domain;
using CobaltMrp.Engine;

namespace CobaltMrp.Api.Api;

public sealed record DetailsRequest(RunCriteria? Criteria, string? Metric, string? Search, int? Take);

public static class RunEndpoints
{
    private static string CsvCell(object? value)
    {
        var v = value switch { null => "", bool b => b ? "yes" : "no", IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture), _ => value.ToString() ?? "" };
        // Neutralise spreadsheet formula injection as well as quoting.
        if (v.Length > 0 && "=+-@".Contains(v[0]) && !decimal.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _)) v = "'" + v;
        return v.Contains(',') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    private static async Task<(MrpRun? Run, ExceptionMessage? Exception)> Find(IRunStore store, Guid runId, Guid exceptionId, CancellationToken ct)
    {
        var run = await store.GetAsync(runId, ct);
        return (run, run?.Exceptions.FirstOrDefault(x => x.ExceptionId == exceptionId));
    }

    public static void MapRuns(this RouteGroupBuilder api)
    {
        api.MapMasterData();

        api.MapGet("/status", (SettingsService settings, IRunStore store, IConfiguration cfg) =>
        {
            var s = settings.Get();
            return Results.Ok(new
            {
                version = typeof(RunEndpoints).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
                storage = store is SqlRunStore ? "sql" : "memory",
                b1Configured = s.B1Db.IsConfigured,
                writebackEnabled = s.ServiceLayer.Enabled && s.ServiceLayer.IsConfigured,
                defaultSource = s.Planning.DefaultSource,
                horizonDays = s.Planning.HorizonDays,
                bucketDays = s.Planning.BucketDays,
            });
        });

        api.MapGet("/warehouses", async (string? source, SettingsService settings, ProviderFactory providers, CancellationToken ct) =>
        {
            var (provider, error) = providers.Create(source);
            if (provider is null) return Results.BadRequest(new { message = error });
            var data = await provider.LoadAsync(Array.Empty<string>(), ct);
            return Results.Ok(data.Stock.Select(x => x.WarehouseCode).Distinct().Order().ToList());
        });

        api.MapGet("/forecasts", async (IForecastStore fc, CancellationToken ct) => await fc.ListAsync(ct));
        api.MapPost("/forecasts", async (ForecastDto? dto, IForecastStore fc, CancellationToken ct) =>
        {
            if (dto is null || string.IsNullOrWhiteSpace(dto.ItemCode) || dto.ItemCode.Length > 50)
                return Results.BadRequest(new { message = "itemCode is required (50 characters at most)." });
            if (string.IsNullOrWhiteSpace(dto.WarehouseCode) || dto.WarehouseCode.Length > 8)
                return Results.BadRequest(new { message = "warehouseCode is required (8 characters at most)." });
            if ((dto.Id?.Length ?? 0) > 32)
                return Results.BadRequest(new { message = "id is too long (32 characters at most)." });
            if (dto.Quantity < 0 || dto.Consumed < 0 || dto.PeriodStart == default)
                return Results.BadRequest(new { message = "periodStart is required; quantity and consumed cannot be negative." });
            return Results.Ok(await fc.UpsertAsync(dto, ct));
        });

        api.MapGet("/runs", async (IRunStore store, CancellationToken ct) => await store.ListAsync(ct));

        // The criteria the wizard starts from: Settings defaults for horizon, forecast and lead-time rules.
        api.MapGet("/runs/defaults", (string? source, RunPlanner planner) => Results.Ok(planner.Defaults(source)));

        // Review step: counts and warnings for a set of criteria, without creating a run.
        api.MapPost("/runs/preview", async (PreviewRequest req, RunPlanner planner, CancellationToken ct) =>
        {
            var (preview, error) = await planner.PreviewAsync(req.Criteria ?? new RunCriteria(), ct);
            return preview is null ? Results.BadRequest(new { message = error }) : Results.Ok(preview);
        });

        // Drill-down behind a number on the Review step (same selectors as the counts).
        api.MapPost("/runs/preview/details", async (DetailsRequest req, RunPlanner planner, CancellationToken ct) =>
        {
            var (detail, error) = await planner.DetailsAsync(req.Criteria ?? new RunCriteria(), req.Metric ?? "", req.Search, req.Take ?? 200, all: false, ct);
            return detail is null ? Results.BadRequest(new { message = error }) : Results.Ok(detail);
        });

        api.MapPost("/runs/preview/details/export", async (DetailsRequest req, RunPlanner planner, CancellationToken ct) =>
        {
            var (detail, error) = await planner.DetailsAsync(req.Criteria ?? new RunCriteria(), req.Metric ?? "", req.Search, 1000, all: true, ct);
            if (detail is null) return Results.BadRequest(new { message = error });
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(string.Join(",", detail.Columns.Select(c => CsvCell(c.Label))));
            foreach (var row in detail.Rows)
                sb.AppendLine(string.Join(",", detail.Columns.Select(c => CsvCell(row.TryGetValue(c.Key, out var v) ? v : null))));
            var bytes = new System.Text.UTF8Encoding(true).GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", $"{detail.Metric}.csv");
        });

        api.MapPost("/runs", async (RunRequest req, HttpContext ctx, IRunStore store, MrpEngine engine,
            RunPlanner planner, CancellationToken ct) =>
        {
            // The old simple body still works: it is mapped onto the Settings defaults.
            var criteria = req.Criteria ?? planner.FromLegacy(req);
            var (prep, error) = await planner.PrepareAsync(criteria, ctx.User.Identity?.Name ?? "system", ct);
            if (prep is null) return Results.BadRequest(new { message = error });

            MrpRun run;
            try { run = engine.Run(prep.Data, prep.Options); }
            // e.g. a BOM that contains itself: tell the planner what is wrong instead of a bare 500
            catch (InvalidOperationException e) { return Results.BadRequest(new { message = e.Message }); }
            run.Notes = req.Notes;
            run.CriteriaJson = prep.Criteria.ToJson();
            await store.AddAsync(run, ct);
            return Results.Ok(RunSummary.From(run));
        });

        api.MapGet("/runs/{id:guid}", async (Guid id, IRunStore store, SettingsService settings, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } r) return Results.NotFound();
            var sl = settings.Get().ServiceLayer;
            return Results.Ok(new RunDetail(RunSummary.From(r),
                r.PlannedOrders.Select(o => PlannedOrderDto.From(o, sl)).ToList(), r.Pegging,
                r.Exceptions.Select(x => ExceptionDto.From(x, r)).ToList(), r.CapacityLoads,
                RunCriteria.FromJson(r.CriteriaJson)));
        });

        api.MapGet("/runs/{id:guid}/pegging/{orderId:guid}", async (Guid id, Guid orderId, IRunStore store, CancellationToken ct) =>
            await store.GetAsync(id, ct) is { } r
                ? Results.Ok(r.Pegging.Where(p => p.PlannedOrderId == orderId))
                : Results.NotFound());

        // Everything dated in the plan, for the Calendar screen. Non-working days are stored with the run as
        // events of kind NonWorking and returned separately.
        api.MapGet("/runs/{id:guid}/calendar", async (Guid id, string from, string to, string? kinds, string? item,
            string? warehouse, IRunStore store, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is not { } run) return Results.NotFound();
            if (!DateOnly.TryParse(from, out var f) || !DateOnly.TryParse(to, out var t) || t < f)
                return Results.BadRequest(new { message = "from and to must be dates (yyyy-MM-dd), from <= to." });
            if (t.DayNumber - f.DayNumber > 400)
                return Results.BadRequest(new { message = "Ask for at most about a year at a time." });

            var wanted = new List<CalendarEventKind>();
            foreach (var k in (kinds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<CalendarEventKind>(k, ignoreCase: true, out var kind))
                    return Results.BadRequest(new { message = $"Unknown kind '{k}'." });
                wanted.Add(kind);
            }
            const int limit = 5000;

            // Non-working days always come back (cheap, and the grid needs them even when filtering kinds).
            var closed = await store.GetEventsAsync(id, new CalendarQuery(f, t, new[] { CalendarEventKind.NonWorking }, null, null, 1000), ct);
            var real = wanted.Where(k => k != CalendarEventKind.NonWorking).ToList();
            var allKinds = Enum.GetValues<CalendarEventKind>().Where(k => k != CalendarEventKind.NonWorking).ToList();
            var events = await store.GetEventsAsync(id, new CalendarQuery(f, t, real.Count > 0 ? real : allKinds, item, warehouse, limit), ct);

            return Results.Ok(new
            {
                from = f, to = t, horizonStart = run.HorizonStart, horizonEnd = run.HorizonEnd,
                truncated = events.Count > limit,
                nonWorking = closed.Select(c => new { date = c.Date, reason = c.Label }),
                events = events.Take(limit).Select(e => new
                {
                    id = e.Id, date = e.Date, kind = e.Kind, itemCode = e.ItemCode, warehouseCode = e.WarehouseCode,
                    quantity = e.Quantity, label = e.Label, @ref = e.Ref, severity = e.Severity,
                }),
            });
        });

        // Resources from the source merged with the capacity overrides saved in Settings, so the Settings
        // screen can list every resource and let the administrator state its real hours.
        api.MapGet("/resources", async (string? source, SettingsService settings, ProviderFactory providers, CancellationToken ct) =>
        {
            var (provider, error) = providers.Create(source);
            if (provider is null) return Results.BadRequest(new { message = error });
            var data = await provider.LoadAsync(Array.Empty<string>(), ct);
            var saved = settings.Get().Planning.Resources.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);
            return Results.Ok(data.WorkCenters.OrderBy(c => c.Code).Select(c =>
            {
                saved.TryGetValue(c.Code, out var r);
                return new
                {
                    code = c.Code, name = c.Name, units = c.Units,
                    hoursPerDay = r?.HoursPerDay ?? c.DailyCapacityHours,
                    efficiencyPercent = r?.EfficiencyPercent ?? c.EfficiencyFactor * 100m,
                    setupHoursPerOrder = r?.SetupHoursPerOrder ?? 0m,
                    configured = r is not null,
                };
            }));
        });

        // Firm the selected planned orders and, when Service Layer write-back is enabled, create the
        // purchase/production orders in SAP B1. State (firmed, B1DocEntry) is persisted either way.
        api.MapPost("/runs/{id:guid}/release", async (Guid id, ReleaseRequest? req, IRunStore store,
            SettingsService settings, ReleaseService release, CancellationToken ct) =>
        {
            if (req?.PlannedOrderIds is null) return Results.BadRequest(new { message = "plannedOrderIds is required." });
            var run = await store.GetAsync(id, ct);
            if (run is null) return Results.NotFound();
            var ids = req.PlannedOrderIds.ToHashSet();
            var selected = run.PlannedOrders.Where(o => ids.Contains(o.Id)).ToList();
            var outcome = await release.ReleaseAsync(store, selected, ct);
            var sl = settings.Get().ServiceLayer;
            return Results.Ok(new
            {
                released = selected.Count, writebackEnabled = outcome.WritebackEnabled, pushedToB1 = outcome.Pushed,
                failures = outcome.Failures.Select(f => new { plannedOrderId = f.PlannedOrderId, error = f.Error }),
                plannedOrders = selected.Select(o => PlannedOrderDto.From(o, sl)).ToList(),
            });
        }).RequireAuthorization("Admin");

        // Execute an exception: release its planned order in SAP B1, or move/close the existing SAP B1 order.
        // Preview reads the live document and changes nothing. Admin only.
        api.MapPost("/runs/{id:guid}/exceptions/{exId:guid}/execute/preview", async (Guid id, Guid exId, ExecuteRequest req,
            IRunStore store, ExceptionExecutor executor, CancellationToken ct) =>
        {
            var (run, ex) = await Find(store, id, exId, ct);
            if (ex is null) return Results.NotFound(new { message = "Exception not found." });
            try { return Results.Ok(await executor.PreviewAsync(run!, ex, req, ct)); }
            catch (ExecuteException e) { return Results.Json(new { message = e.Message }, statusCode: e.Status); }
        }).RequireAuthorization("Admin");

        api.MapPost("/runs/{id:guid}/exceptions/{exId:guid}/execute", async (Guid id, Guid exId, ExecuteRequest req, HttpContext ctx,
            IRunStore store, ExceptionExecutor executor, ILogger<ExceptionExecutor> log, CancellationToken ct) =>
        {
            var (run, ex) = await Find(store, id, exId, ct);
            if (ex is null) return Results.NotFound(new { message = "Exception not found." });
            var user = ctx.User.Identity?.Name ?? "";
            try
            {
                var result = await executor.ExecuteAsync(store, run!, ex, req, user, ct);
                log.LogInformation("Exception {Id} executed by {User}: {Message}", exId, user, result.Message);
                return Results.Ok(result);
            }
            catch (ExecuteException e)
            {
                log.LogWarning("Exception {Id} execute refused/failed ({Status}) for {User}: {Message}", exId, e.Status, user, e.Message);
                return Results.Json(new { message = e.Message }, statusCode: e.Status);
            }
        }).RequireAuthorization("Admin");
    }
}

/// <summary>Builds the planning data source from the live settings (no restart needed after a change).</summary>
public sealed class ProviderFactory(SettingsService settings, MockB1DataProvider mock)
{
    public (IB1DataProvider? Provider, string? Error) Create(string? source)
    {
        var s = settings.Get();
        // No source given (e.g. an edit sent without one) means the data source chosen in Settings.
        if ((string.IsNullOrWhiteSpace(source) ? s.Planning.DefaultSource : source) != "b1-sql") return (mock, null);
        var cs = s.B1Db.BuildConnectionString(readOnlyIntent: true);
        return cs is null
            ? (null, "The SAP B1 database is not configured. Open Settings and fill in the SAP B1 database section.")
            : (new B1SqlDataProvider(cs, s.Planning.HoursPerUnitPerDay), null);
    }

    /// <summary>The cheap master-data reader (items, groups, suppliers) for the same source.</summary>
    public (IMasterDataProvider? Provider, string? Error) CreateMaster(string? source)
    {
        var s = settings.Get();
        if ((string.IsNullOrWhiteSpace(source) ? s.Planning.DefaultSource : source) != "b1-sql") return (mock, null);
        var cs = s.B1Db.BuildConnectionString(readOnlyIntent: true);
        return cs is null
            ? (null, "The SAP B1 database is not configured. Open Settings and fill in the SAP B1 database section.")
            : (new B1SqlDataProvider(cs, s.Planning.HoursPerUnitPerDay), null);
    }
}
