using CobaltMrp.Api.Api;
using CobaltMrp.Api.Settings;
using CobaltMrp.Domain;
using CobaltMrp.Engine;

namespace CobaltMrp.Api.Services;

/// <summary>Everything an engine run needs, plus the scope that decides which results are kept.</summary>
public sealed record PreparedRun(PlanningDataSet Data, MrpOptions Options, HashSet<string>? Scope, RunCriteria Criteria);

public sealed record PreviewResult(
    PreviewItems Items, PreviewDemand Demand, PreviewSupply Supply, PreviewLeadTime LeadTime, List<string> Warnings, PreviewPlanning Planning);
public sealed record PreviewPlanning(
    string Level, List<string> Warehouses, string PurchaseDocument, bool TransfersEnabled, List<string> TransferSources, int TransferLeadTimeDays);
public sealed record PreviewItems(int Total, int InScope, int Buy, int Make, int NoSupplier, int NoLeadTime);
public sealed record PreviewDemand(int SalesOrderLines, decimal SalesOrderQuantity, int PastDueLines, int ForecastLines, int ProductionComponentLines);
public sealed record PreviewSupply(int PurchaseOrderLines, int ProductionOrderLines, int StockRows);
public sealed record PreviewLeadTime(int FromSupplier, int FromItem, int FromItemGroup, int Defaulted);

/// <summary>
/// The one place that turns wizard criteria into a plan: load, layer the planner's overrides and the lead-time
/// rule, apply the criteria filters, then decide the scope. Used by both the real run and the Review preview, so
/// they can never disagree about what a run will do.
/// </summary>
public sealed class RunPlanner
{
    private readonly SettingsService _settings;
    private readonly ProviderFactory _providers;
    private readonly IForecastStore _forecasts;
    private readonly IMasterStore _master;

    public RunPlanner(SettingsService settings, ProviderFactory providers, IForecastStore forecasts, IMasterStore master)
    { _settings = settings; _providers = providers; _forecasts = forecasts; _master = master; }

    /// <summary>Criteria pre-filled from Settings: what the wizard starts from.</summary>
    public RunCriteria Defaults(string? source)
    {
        var p = _settings.Get().Planning;
        var c = new RunCriteria { Source = source is "mock" or "b1-sql" ? source : p.DefaultSource };
        c.Horizon.Days = p.HorizonDays;
        c.Horizon.BucketDays = p.BucketDays;
        c.Demand.ForecastConsumption = p.ForecastConsumption == "None" ? ForecastConsumptionMode.None : ForecastConsumptionMode.Forward;
        c.Demand.ForecastLookbackDays = p.ForecastLookbackDays;
        c.LeadTime.Mode = p.LeadTimeMode == "Longest" ? LeadTimeMode.Longest : LeadTimeMode.FirstAvailable;
        c.LeadTime.DefaultDays = p.DefaultLeadTimeDays;
        c.Level = p.DefaultLevel == "Company" ? PlanningLevel.Company : PlanningLevel.Warehouse;
        c.Purchasing.Document = p.PurchaseDocument == "Request" ? PurchaseDocument.Request : PurchaseDocument.Order;
        c.Purchasing.Grouping = Enum.TryParse<DocumentGrouping>(p.DocumentGrouping, true, out var grouping) ? grouping : DocumentGrouping.Supplier;
        c.Transfers.LeadTimeDays = p.TransferLeadTimeDays;
        c.LeadTime.Priority = p.LeadTimePriority
            .Select(n => Enum.TryParse<LeadTimeSourceKind>(n, true, out var k) ? (LeadTimeSourceKind?)k : null)
            .Where(k => k is not null).Select(k => k!.Value).ToList();
        c.Validate();   // fills in any missing lead-time source
        return c;
    }

    /// <summary>The old simple request body, mapped onto the new criteria.</summary>
    public RunCriteria FromLegacy(RunRequest req)
    {
        var c = Defaults(req.Source);
        if (req.HorizonDays is { } d) c.Horizon.Days = d;
        if (req.BucketDays is { } b) c.Horizon.BucketDays = b;
        if (req.Warehouses is { Length: > 0 } w) c.Scope.Warehouses = w.ToList();
        return c;
    }

    /// <summary>Load and shape everything for a run. Returns an error message instead when the criteria cannot be used.</summary>
    public async Task<(PreparedRun? Run, string? Error)> PrepareAsync(RunCriteria criteria, string runBy, CancellationToken ct)
    {
        var c = criteria.Clone();
        if (c.Validate() is { } invalid) return (null, invalid);

        var (provider, error) = _providers.Create(c.Source);
        if (provider is null) return (null, error);

        var s = _settings.Get();
        var loaded = await provider.LoadAsync(c.Scope.Warehouses, ct);

        // Forecast source: a B1 run uses B1's own forecasts (OFCT/FCT1). The mock source uses the
        // Cobalt-managed forecasts from the Forecasts screen, falling back to the mock's own if none exist.
        var cobaltForecasts = c.Source == "b1-sql"
            ? new List<Forecast>()
            : (await _forecasts.ListAsync(ct)).Select(f => f.ToForecast()).ToList();
        var (centers, routings) = PlanningSetup.ApplyResources(loaded.WorkCenters, loaded.Routings, s.Planning);
        var data = new PlanningDataSet
        {
            Items = loaded.Items, Stock = loaded.Stock, Bom = loaded.Bom, Routings = routings,
            WorkCenters = centers, Supply = loaded.Supply, Demand = loaded.Demand,
            Forecasts = cobaltForecasts.Count > 0 ? cobaltForecasts : loaded.Forecasts,
            Calendar = PlanningSetup.BuildCalendar(loaded.Calendar, s.Planning),
        };

        // Layer the planner's overrides, pick each item's supplier and apply the lead-time rule.
        data = PlanningOverlay.Apply(data, await _master.GetItemOverridesAsync(ct), await _master.GetSupplierSettingsAsync(ct), c.LeadTime);

        if (c.Transfers.Enabled && EffectiveWarehouses(data, c).Count < 2)
            return (null, "Transfers need at least two warehouses with planned stock or demand. Choose more warehouses or turn transfers off.");

        var today = DateOnly.FromDateTime(DateTime.Today);
        var horizonStart = c.Horizon.StartDate ?? today;
        data = CriteriaApplier.PrepareData(data, c, horizonStart);
        var scope = CriteriaApplier.Scope(data, c);
        return (new PreparedRun(data, CriteriaApplier.ToOptions(c, today, runBy, scope), scope, c), null);
    }

    private static List<string> EffectiveWarehouses(PlanningDataSet data, RunCriteria c) =>
        c.Scope.Warehouses.Count > 0
            ? c.Scope.Warehouses.ToList()
            : data.Stock.Where(x => x.PlanningEnabled).Select(x => x.WarehouseCode)
                .Concat(data.Demand.Select(x => x.WarehouseCode)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Counts and warnings for the wizard's Review step. Creates nothing.</summary>
    public async Task<(PreviewResult? Preview, string? Error)> PreviewAsync(RunCriteria criteria, CancellationToken ct)
    {
        var (prep, error) = await PrepareAsync(criteria, "preview", ct);
        if (prep is null) return (null, error);

        int C(string id) => PreviewMetrics.Count(prep, id);

        var items = new PreviewItems(C("items.total"), C("items.inScope"), C("items.buy"), C("items.make"),
            C("items.noSupplier"), C("items.noLeadTime"));
        var demand = new PreviewDemand(C("demand.salesOrders"), PreviewMetrics.SalesOrderQuantity(prep), C("demand.pastDue"),
            C("demand.forecasts"), C("demand.productionComponents"));
        var supply = new PreviewSupply(C("supply.purchaseOrders"), C("supply.productionOrders"), C("supply.stock"));
        var lead = new PreviewLeadTime(C("leadTime.supplier"), C("leadTime.item"), C("leadTime.itemGroup"), C("leadTime.defaulted"));

        var warnings = new List<string>();
        if (items.InScope == 0) warnings.Add("No items match the scope you chose: this run would plan nothing.");
        if (items.NoSupplier > 0)
            warnings.Add($"{items.NoSupplier} purchased item(s) in scope have no supplier: their orders cannot be released to SAP B1 until a supplier is set (Master data).");
        if (items.NoLeadTime > 0)
            warnings.Add($"{items.NoLeadTime} purchased item(s) in scope have no lead time from any source, so they are planned with none (Master data, or set a default lead time).");
        if (demand.PastDueLines > 0)
            warnings.Add(prep.Criteria.Demand.PastDue == PastDueMode.Ignore
                ? $"{demand.PastDueLines} overdue demand line(s) are ignored."
                : $"{demand.PastDueLines} demand line(s) are already overdue and will be planned as due on day one.");
        if (prep.Criteria.Source == "b1-sql" && prep.Criteria.Demand.Forecasts && demand.ForecastLines == 0)
            warnings.Add("Forecasts are on but no current forecast lines were found.");
        var cr = prep.Criteria;
        var whs = EffectiveWarehouses(prep.Data, cr);
        if (cr.Level == PlanningLevel.Company)
            warnings.Add($"Company level: stock in {whs.Count} warehouse(s) is pooled; orders are received in each item's default warehouse.");
        var planning = new PreviewPlanning(cr.Level.ToString(), whs, cr.Purchasing.Document.ToString(), prep.Options.TransferEnabled,
            cr.Transfers.SourceWarehouses.Count > 0 ? cr.Transfers.SourceWarehouses : whs, cr.Transfers.LeadTimeDays);
        return (new PreviewResult(items, demand, supply, lead, warnings, planning), null);
    }

    /// <summary>The records behind one Review-step number, from the same selectors as the counts.</summary>
    public async Task<(MetricDetail? Detail, string? Error)> DetailsAsync(RunCriteria criteria, string metric, string? search, int take, bool all, CancellationToken ct)
    {
        if (!PreviewMetrics.Ids.Contains(metric, StringComparer.OrdinalIgnoreCase))
            return (null, $"Unknown metric '{metric}'.");
        var (prep, error) = await PrepareAsync(criteria, "preview", ct);
        if (prep is null) return (null, error);
        var id = PreviewMetrics.Ids.First(i => string.Equals(i, metric, StringComparison.OrdinalIgnoreCase));
        return (PreviewMetrics.Detail(prep, id, search, take, all), null);
    }
}
