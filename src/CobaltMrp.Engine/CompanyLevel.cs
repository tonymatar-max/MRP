using CobaltMrp.Domain;

namespace CobaltMrp.Engine;

/// <summary>
/// Company-level planning: the stock, open orders, demand and forecasts of all selected warehouses are pooled per item
/// and the item is planned once. Afterwards every order is attached to the warehouse that should receive it: the item's
/// default warehouse, otherwise the selected warehouse with the most stock and demand for it.
/// </summary>
public static class CompanyLevel
{
    public const string Pool = "ALL";

    public static (PlanningDataSet Pooled, Dictionary<string, string> Receiving) Collapse(PlanningDataSet data, MrpOptions opt)
    {
        var filter = new HashSet<string>(opt.Warehouses, StringComparer.OrdinalIgnoreCase);
        bool Allowed(string wh) => filter.Count == 0 || filter.Contains(wh);

        var stock = data.Stock.Where(s => Allowed(s.WarehouseCode) && s.PlanningEnabled).ToList();
        var demand = data.Demand.Where(d => Allowed(d.WarehouseCode)).ToList();

        // Where each item's orders should be received.
        var weight = new Dictionary<(string Item, string Wh), decimal>(TupleKeyComparer.Instance);
        void Add(string item, string wh, decimal w) { var k = (item, wh); weight[k] = weight.GetValueOrDefault(k) + w; }
        foreach (var s in stock) Add(s.ItemCode, s.WarehouseCode, Math.Abs(s.OnHand) + 1m);       // +1 so a zero row still counts as a location
        foreach (var d in demand) Add(d.ItemCode, d.WarehouseCode, d.Quantity);

        var receiving = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.Items)
        {
            string? pick = !string.IsNullOrWhiteSpace(item.HomeWarehouse) && Allowed(item.HomeWarehouse) ? item.HomeWarehouse : null;
            pick ??= weight.Where(kv => string.Equals(kv.Key.Item, item.ItemCode, StringComparison.OrdinalIgnoreCase))
                           .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Wh, StringComparer.OrdinalIgnoreCase)
                           .Select(kv => kv.Key.Wh).FirstOrDefault();
            // An item reached only through a BOM can have no stock row or demand of its own: still give it a real
            // warehouse (never leave an order in the internal pool): first selected warehouse, else first one in the data.
            pick ??= opt.Warehouses.FirstOrDefault(w => !string.IsNullOrWhiteSpace(w))
                     ?? data.Stock.Select(x => x.WarehouseCode).Concat(data.Demand.Select(x => x.WarehouseCode))
                            .Where(w => !string.IsNullOrWhiteSpace(w)).OrderBy(w => w, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (pick is not null) receiving[item.ItemCode] = pick;
        }

        var pooledStock = stock
            .GroupBy(s => s.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => new WarehouseStock
            {
                ItemCode = g.Key, WarehouseCode = Pool, OnHand = g.Sum(x => x.OnHand),
                Committed = g.Sum(x => x.Committed), Ordered = g.Sum(x => x.Ordered), PlanningEnabled = true,
            }).ToList();

        var pooled = new PlanningDataSet
        {
            Items = data.Items,
            Stock = pooledStock,
            Bom = data.Bom.Select(b => new BomComponent
            {
                ParentItemCode = b.ParentItemCode, ComponentItemCode = b.ComponentItemCode,
                QuantityPer = b.QuantityPer, ScrapFactor = b.ScrapFactor, WarehouseCode = null,
            }).ToList(),
            Routings = data.Routings, WorkCenters = data.WorkCenters,
            Supply = data.Supply.Where(s => Allowed(s.WarehouseCode)).Select(s => new SupplyOrder
            {
                ItemCode = s.ItemCode, WarehouseCode = Pool, Type = s.Type, Quantity = s.Quantity,
                DueDate = s.DueDate, SourceDocKey = s.SourceDocKey, SourceLine = s.SourceLine,
            }).ToList(),
            Demand = demand.Select(d => new DemandOrder
            {
                ItemCode = d.ItemCode, WarehouseCode = Pool, Source = d.Source, Quantity = d.Quantity,
                DueDate = d.DueDate, SourceDocKey = d.SourceDocKey,
            }).ToList(),
            Forecasts = data.Forecasts.Where(f => Allowed(f.WarehouseCode)).Select(f => new Forecast
            {
                ItemCode = f.ItemCode, WarehouseCode = Pool, PeriodStart = f.PeriodStart, Quantity = f.Quantity, Consumed = f.Consumed,
            }).ToList(),
            Calendar = data.Calendar,
        };
        return (pooled, receiving);
    }

    public static MrpOptions PooledOptions(MrpOptions o) => new()
    {
        HorizonStart = o.HorizonStart, HorizonDays = o.HorizonDays, BucketDays = o.BucketDays,
        Warehouses = Array.Empty<string>(), UseForecasts = o.UseForecasts, ConsumeForecast = o.ConsumeForecast,
        ForecastLookbackDays = o.ForecastLookbackDays, RunCapacityPlanning = o.RunCapacityPlanning, ItemScope = o.ItemScope,
        CompanyLevel = false, PurchaseDocument = o.PurchaseDocument, TransferEnabled = false, RunBy = o.RunBy,
    };

    /// <summary>Replace the pool with the real warehouse that receives each item's orders.</summary>
    public static void Remap(MrpRun run, Dictionary<string, string> receiving)
    {
        string? Wh(string item) => receiving.TryGetValue(item, out var w) ? w : null;
        foreach (var o in run.PlannedOrders) if (o.WarehouseCode == Pool) o.WarehouseCode = Wh(o.ItemCode) ?? Pool;
        foreach (var e in run.Exceptions) if (e.WarehouseCode == Pool) e.WarehouseCode = Wh(e.ItemCode);
        foreach (var e in run.Events) if (e.WarehouseCode == Pool) e.WarehouseCode = Wh(e.ItemCode);
    }

    private sealed class TupleKeyComparer : IEqualityComparer<(string Item, string Wh)>
    {
        public static readonly TupleKeyComparer Instance = new();
        public bool Equals((string Item, string Wh) x, (string Item, string Wh) y) =>
            string.Equals(x.Item, y.Item, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Wh, y.Wh, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Item, string Wh) o) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(o.Item), StringComparer.OrdinalIgnoreCase.GetHashCode(o.Wh));
    }
}
