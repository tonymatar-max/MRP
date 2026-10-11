using System.Globalization;
using CobaltMrp.Domain;

namespace CobaltMrp.Api.Services;

public sealed record MetricColumn(string Key, string Label, string Type);
public sealed record MetricStat(string Label, string Value);

/// <summary>The records behind one number on the wizard's Review step.</summary>
public sealed class MetricDetail
{
    public string Metric { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public int Total { get; init; }
    public int Matched { get; init; }
    public int Shown => Rows.Count;
    public bool Truncated => Matched > Shown;
    public List<MetricStat> Summary { get; init; } = new();
    public List<MetricColumn> Columns { get; init; } = new();
    public List<Dictionary<string, object?>> Rows { get; init; } = new();
}

/// <summary>
/// One definition per Review-step number. The preview counts and the drill-down lists both come from
/// these selectors, so the number you click and the rows you see cannot disagree.
/// </summary>
public static class PreviewMetrics
{
    public static readonly string[] Ids =
    {
        "items.total", "items.inScope", "items.buy", "items.make", "items.noSupplier", "items.noLeadTime",
        "demand.salesOrders", "demand.pastDue", "demand.forecasts", "demand.productionComponents",
        "supply.purchaseOrders", "supply.productionOrders", "supply.stock",
        "leadTime.supplier", "leadTime.item", "leadTime.itemGroup", "leadTime.defaulted",
    };

    private abstract class Metric
    {
        public string Title = "", Description = "";
        public abstract int Count { get; }
        public abstract MetricDetail Detail(string id, string? search, int take, int? limitAll);
    }

    private sealed class Metric<T>(IReadOnlyList<T> source, List<MetricColumn> columns, Func<T, Dictionary<string, object?>> map,
        Func<IReadOnlyList<T>, List<MetricStat>> summary) : Metric
    {
        public override int Count => source.Count;

        public override MetricDetail Detail(string id, string? search, int take, int? limitAll)
        {
            IEnumerable<Dictionary<string, object?>> rows = source.Select(map);
            if (!string.IsNullOrWhiteSpace(search))
                rows = rows.Where(r => r.Values.Any(v => v is string s && s.Contains(search, StringComparison.OrdinalIgnoreCase)));
            var matched = rows.ToList();
            var page = matched.Take(limitAll ?? take).ToList();
            return new MetricDetail
            {
                Metric = id, Title = Title, Description = Description, Total = source.Count, Matched = matched.Count,
                Summary = summary(source), Columns = columns, Rows = page,
            };
        }
    }

    // ---- Public API ------------------------------------------------------------------------------------------------

    public static int Count(PreparedRun prep, string id) => Resolve(prep, id)?.Count ?? 0;

    public static decimal SalesOrderQuantity(PreparedRun prep) =>
        prep.Data.Demand.Where(d => d.Source == DemandSource.SalesOrder).Sum(d => d.Quantity);

    /// <summary>Null when the metric id is unknown. <paramref name="limitAll"/> = return every matching row (CSV export).</summary>
    public static MetricDetail? Detail(PreparedRun prep, string id, string? search, int take, bool all = false)
    {
        var m = Resolve(prep, id);
        return m?.Detail(id, search, Math.Clamp(take, 1, 1000), all ? int.MaxValue : null);
    }

    // ---- Definitions ---------------------------------------------------------------------------------------------

    private static Metric? Resolve(PreparedRun prep, string id)
    {
        var d = prep.Data;
        var start = prep.Options.HorizonStart;
        var names = d.Items.ToDictionary(i => i.ItemCode, i => i.ItemName, StringComparer.OrdinalIgnoreCase);
        string? Name(string code) => names.TryGetValue(code, out var n) ? n : null;

        var inScope = prep.Scope is null ? d.Items.ToList() : d.Items.Where(i => prep.Scope.Contains(i.ItemCode)).ToList();
        var buy = inScope.Where(i => i.Procurement == ProcurementType.Buy).ToList();

        Metric Items(string title, string desc, IReadOnlyList<PlanItem> src) =>
            new Metric<PlanItem>(src, ItemColumns, ItemRow, ItemSummary) { Title = title, Description = desc };

        Metric Demand(string title, string desc, IReadOnlyList<DemandOrder> src) =>
            new Metric<DemandOrder>(src, DemandColumns,
                x => new()
                {
                    ["document"] = x.SourceDocKey, ["itemCode"] = x.ItemCode, ["itemName"] = Name(x.ItemCode), ["warehouse"] = x.WarehouseCode,
                    ["kind"] = x.Source == DemandSource.SalesOrder ? "Sales order" : "Production component",
                    ["quantity"] = x.Quantity, ["dueDate"] = Iso(x.DueDate), ["overdue"] = x.DueDate < start,
                },
                list => DocSummary(list.Select(x => (x.Quantity, x.DueDate, x.ItemCode)), start, "Lines"))
            { Title = title, Description = desc };

        Metric Supply(string title, string desc, IReadOnlyList<SupplyOrder> src) =>
            new Metric<SupplyOrder>(src, SupplyColumns,
                x => new()
                {
                    ["document"] = x.SourceDocKey, ["line"] = x.SourceLine, ["itemCode"] = x.ItemCode, ["itemName"] = Name(x.ItemCode),
                    ["warehouse"] = x.WarehouseCode, ["quantity"] = x.Quantity, ["dueDate"] = Iso(x.DueDate), ["overdue"] = x.DueDate < start,
                },
                list => DocSummary(list.Select(x => (x.Quantity, x.DueDate, x.ItemCode)), start, "Lines"))
            { Title = title, Description = desc };

        Metric LeadTime(string title, string desc, Func<PlanItem, bool> pick) =>
            Items(title, desc, buy.Where(pick).ToList());

        return id switch
        {
            "items.total" => Items("All items", "Every item the data source offers.", d.Items.ToList()),
            "items.inScope" => Items("Items in scope", "The items this run will plan.", inScope),
            "items.buy" => Items("Purchased items in scope", "In-scope items that are bought.", buy),
            "items.make" => Items("Made items in scope", "In-scope items that are produced.", inScope.Where(i => i.Procurement == ProcurementType.Make).ToList()),
            "items.noSupplier" => Items("Purchased items with no supplier",
                "Their orders cannot be released to SAP B1 until a supplier is set in Master data.",
                buy.Where(i => string.IsNullOrWhiteSpace(i.PreferredVendor)).ToList()),
            "items.noLeadTime" => Items("Purchased items with no lead time",
                "No lead time from the supplier, the item or the item group, so they are planned with none (or the default).",
                buy.Where(i => i.LeadTimeSource == "None").ToList()),

            "demand.salesOrders" => Demand("Open sales order lines", "Sales order lines still to be delivered.",
                d.Demand.Where(x => x.Source == DemandSource.SalesOrder).ToList()),
            "demand.pastDue" => Demand("Overdue demand lines",
                prep.Criteria.Demand.PastDue == PastDueMode.Ignore
                    ? "Demand already due before the plan starts. These are ignored by this run."
                    : "Demand already due before the plan starts. It is planned as due on day one.",
                d.Demand.Where(x => x.DueDate < start).ToList()),
            "demand.productionComponents" => Demand("Components of open production orders",
                "Components still to be issued to production orders that already exist in SAP B1.",
                d.Demand.Where(x => x.Source == DemandSource.DependentDemand).ToList()),
            "demand.forecasts" => new Metric<Forecast>(d.Forecasts.ToList(), ForecastColumns,
                f => new()
                {
                    ["itemCode"] = f.ItemCode, ["itemName"] = Name(f.ItemCode), ["warehouse"] = f.WarehouseCode,
                    ["periodStart"] = Iso(f.PeriodStart), ["quantity"] = f.Quantity, ["consumed"] = f.Consumed,
                },
                list => new List<MetricStat>
                {
                    new("Lines", N(list.Count)), new("Quantity", N(list.Sum(x => x.Quantity))),
                    new("Items", N(list.Select(x => x.ItemCode).Distinct(StringComparer.OrdinalIgnoreCase).Count())),
                    new("Periods", list.Count == 0 ? "-" : $"{Iso(list.Min(x => x.PeriodStart))} to {Iso(list.Max(x => x.PeriodStart))}"),
                }) { Title = "Forecast lines", Description = "Forecast quantities the plan can use (before consumption by sales orders)." },

            "supply.purchaseOrders" => Supply("Open purchase order lines", "Purchase orders already placed; they count as supply on their due date.",
                d.Supply.Where(x => x.Type == SupplyType.PurchaseOrder).ToList()),
            "supply.productionOrders" => Supply("Open production orders", "Production orders already planned or released; they count as supply on their due date.",
                d.Supply.Where(x => x.Type == SupplyType.ProductionOrder).ToList()),
            "supply.stock" => new Metric<WarehouseStock>(d.Stock.ToList(), StockColumns,
                s => new()
                {
                    ["itemCode"] = s.ItemCode, ["itemName"] = Name(s.ItemCode), ["warehouse"] = s.WarehouseCode,
                    ["onHand"] = s.OnHand, ["committed"] = s.Committed, ["onOrder"] = s.Ordered,
                },
                list => new List<MetricStat>
                {
                    new("Rows", N(list.Count)), new("On hand", N(list.Sum(x => x.OnHand))),
                    new("Positions with stock", N(list.Count(x => x.OnHand != 0))),
                    new("Warehouses", N(list.Select(x => x.WarehouseCode).Distinct(StringComparer.OrdinalIgnoreCase).Count())),
                }) { Title = "Stock positions", Description = "Item by warehouse positions the plan starts from." },

            "leadTime.supplier" => LeadTime("Lead time from the supplier", "Purchased items whose lead time comes from the supplier's lead time.", i => i.LeadTimeSource == "Supplier"),
            "leadTime.item" => LeadTime("Lead time from the item", "Purchased items using the item's own lead time (override or SAP B1).", i => i.LeadTimeSource == "Item"),
            "leadTime.itemGroup" => LeadTime("Lead time from the item group", "Purchased items using the item group's lead time in SAP B1.", i => i.LeadTimeSource == "ItemGroup"),
            "leadTime.defaulted" => LeadTime("Lead time defaulted", "Purchased items with no lead time from any source: the default is used (0 days unless you set one).",
                i => i.LeadTimeSource is "Default" or "None"),
            _ => null,
        };
    }

    // ---- Columns, rows, summaries --------------------------------------------------------------------------------

    private static readonly List<MetricColumn> ItemColumns = new()
    {
        new("itemCode", "Item", "text"), new("itemName", "Name", "text"), new("group", "Group", "text"),
        new("procurement", "Type", "badge"), new("method", "Method", "badge"), new("supplier", "Supplier", "text"),
        new("supplierSource", "Supplier from", "badge"), new("leadTimeDays", "Lead time (d)", "number"), new("leadTimeSource", "Lead time from", "badge"),
    };

    private static Dictionary<string, object?> ItemRow(PlanItem i) => new()
    {
        ["itemCode"] = i.ItemCode, ["itemName"] = i.ItemName, ["group"] = i.GroupCode,
        ["procurement"] = i.Procurement.ToString(), ["method"] = i.PlanningMethod.ToString(),
        ["supplier"] = i.PreferredVendor,
        ["supplierSource"] = i.SupplierSource switch { "master-data" => "Master data", "b1-preferred" => "SAP B1", _ => null },
        ["leadTimeDays"] = i.LeadTimeDays, ["leadTimeSource"] = i.LeadTimeSource switch { "ItemGroup" => "Item group", var s => s },
    };

    private static List<MetricStat> ItemSummary(IReadOnlyList<PlanItem> list) => new()
    {
        new("Items", N(list.Count)),
        new("Purchased", N(list.Count(i => i.Procurement == ProcurementType.Buy))),
        new("Made", N(list.Count(i => i.Procurement == ProcurementType.Make))),
        new("With a supplier", N(list.Count(i => !string.IsNullOrWhiteSpace(i.PreferredVendor)))),
        new("Suppliers", N(list.Where(i => !string.IsNullOrWhiteSpace(i.PreferredVendor)).Select(i => i.PreferredVendor!).Distinct(StringComparer.OrdinalIgnoreCase).Count())),
    };

    private static readonly List<MetricColumn> DemandColumns = new()
    {
        new("document", "Document", "text"), new("itemCode", "Item", "text"), new("itemName", "Name", "text"), new("warehouse", "Wh", "text"),
        new("kind", "Kind", "badge"), new("quantity", "Quantity", "number"), new("dueDate", "Due", "date"), new("overdue", "Overdue", "flag"),
    };

    private static readonly List<MetricColumn> SupplyColumns = new()
    {
        new("document", "Document", "text"), new("line", "Line", "number"), new("itemCode", "Item", "text"), new("itemName", "Name", "text"),
        new("warehouse", "Wh", "text"), new("quantity", "Quantity", "number"), new("dueDate", "Due", "date"), new("overdue", "Overdue", "flag"),
    };

    private static readonly List<MetricColumn> ForecastColumns = new()
    {
        new("itemCode", "Item", "text"), new("itemName", "Name", "text"), new("warehouse", "Wh", "text"),
        new("periodStart", "Period start", "date"), new("quantity", "Quantity", "number"), new("consumed", "Consumed", "number"),
    };

    private static readonly List<MetricColumn> StockColumns = new()
    {
        new("itemCode", "Item", "text"), new("itemName", "Name", "text"), new("warehouse", "Wh", "text"),
        new("onHand", "On hand", "number"), new("committed", "Committed", "number"), new("onOrder", "On order", "number"),
    };

    private static List<MetricStat> DocSummary(IEnumerable<(decimal Qty, DateOnly Due, string Item)> rows, DateOnly start, string linesLabel)
    {
        var list = rows.ToList();
        if (list.Count == 0) return new() { new(linesLabel, "0") };
        return new()
        {
            new(linesLabel, N(list.Count)), new("Quantity", N(list.Sum(x => x.Qty))),
            new("Items", N(list.Select(x => x.Item).Distinct(StringComparer.OrdinalIgnoreCase).Count())),
            new("Earliest due", Iso(list.Min(x => x.Due))), new("Latest due", Iso(list.Max(x => x.Due))),
            new("Overdue", N(list.Count(x => x.Due < start))),
        };
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string N(decimal v) => v.ToString("#,0.##", CultureInfo.InvariantCulture);
    private static string N(int v) => v.ToString("#,0", CultureInfo.InvariantCulture);
}
