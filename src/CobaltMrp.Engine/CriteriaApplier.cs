using CobaltMrp.Domain;

namespace CobaltMrp.Engine;

/// <summary>Turns the wizard's <see cref="RunCriteria"/> into a filtered dataset, engine options and an output scope.</summary>
public static class CriteriaApplier
{
    /// <summary>
    /// Input filters: which demand and supply the plan is allowed to see, and which planning rules apply. The
    /// <em>scope</em> (which items get planned) is separate: see <see cref="Scope"/>.
    /// </summary>
    public static PlanningDataSet PrepareData(PlanningDataSet data, RunCriteria c, DateOnly horizonStart)
    {
        var d = c.Demand;

        IEnumerable<DemandOrder> demand = data.Demand;
        if (!d.SalesOrders) demand = demand.Where(x => x.Source != DemandSource.SalesOrder);
        if (!d.ProductionComponents) demand = demand.Where(x => x.Source != DemandSource.DependentDemand);
        if (d.SalesOrderFrom is { } from) demand = demand.Where(x => x.Source != DemandSource.SalesOrder || x.DueDate >= from);
        if (d.SalesOrderTo is { } to) demand = demand.Where(x => x.Source != DemandSource.SalesOrder || x.DueDate <= to);
        if (d.PastDue == PastDueMode.Ignore) demand = demand.Where(x => x.DueDate >= horizonStart);

        IEnumerable<SupplyOrder> supply = data.Supply;
        if (!c.Supply.OpenPurchaseOrders) supply = supply.Where(x => x.Type != SupplyType.PurchaseOrder);
        if (!c.Supply.OpenProductionOrders) supply = supply.Where(x => x.Type != SupplyType.ProductionOrder);

        IEnumerable<WarehouseStock> stock = data.Stock;
        if (!c.Supply.OnHand)
            stock = stock.Select(s => new WarehouseStock
            {
                ItemCode = s.ItemCode, WarehouseCode = s.WarehouseCode, OnHand = 0m,
                Committed = s.Committed, Ordered = s.Ordered, PlanningEnabled = s.PlanningEnabled,
            });

        // Items: safety stock applies to MRP-planned items (min/max and reorder-point ARE a stock policy); lot-size
        // constraints can be switched off. Items are cloned so the source data is never modified.
        var items = data.Items.Select(i =>
        {
            var n = CloneItem(i);
            if (!d.SafetyStock && n.PlanningMethod == PlanningMethod.Mrp) n.SafetyStock = 0m;
            if (!c.LotSizing.ApplyMinOrder) n.MinOrderQty = 0m;
            if (!c.LotSizing.ApplyMultiples) n.OrderMultiple = 0m;
            return n;
        }).ToList();

        return new PlanningDataSet
        {
            Items = items, Stock = stock.ToList(), Bom = data.Bom, Routings = data.Routings, WorkCenters = data.WorkCenters,
            Supply = supply.ToList(), Demand = demand.ToList(),
            Forecasts = d.Forecasts ? data.Forecasts : Array.Empty<Forecast>(),
            Calendar = c.Calendar.UseWorkingCalendar ? data.Calendar : WorkCalendar.AllDays,
        };
    }

    public static MrpOptions ToOptions(RunCriteria c, DateOnly today, string runBy, IReadOnlyCollection<string>? scope) => new()
    {
        HorizonStart = c.Horizon.StartDate ?? today,
        HorizonDays = c.Horizon.Days,
        BucketDays = c.Horizon.BucketDays,
        Warehouses = c.Scope.Warehouses,
        UseForecasts = c.Demand.Forecasts,
        ConsumeForecast = c.Demand.ForecastConsumption == ForecastConsumptionMode.Forward,
        ForecastLookbackDays = c.Demand.ForecastLookbackDays,
        RunCapacityPlanning = c.Capacity.Enabled,
        ItemScope = scope is null ? null : new HashSet<string>(scope, StringComparer.OrdinalIgnoreCase),
        CompanyLevel = c.Level == PlanningLevel.Company,
        PurchaseDocument = c.Purchasing.Document,
        TransferEnabled = c.Level == PlanningLevel.Warehouse && c.Transfers.Enabled,
        TransferSources = c.Transfers.SourceWarehouses,
        TransferLeadDays = c.Transfers.LeadTimeDays,
        RunBy = runBy,
    };

    /// <summary>
    /// The items whose planned orders the run should keep, or null for "everything". Demand always flows through
    /// the whole BOM so a purchased item is planned correctly even when only its supplier is selected; the scope
    /// only decides which results are kept.
    /// </summary>
    public static HashSet<string>? Scope(PlanningDataSet data, RunCriteria c)
    {
        var s = c.Scope;
        var restricted = s.ItemGroups.Count > 0 || s.Suppliers.Count > 0 || s.ItemSearch.Length > 0 ||
                         s.ItemCodes.Count > 0 || s.Procurement != ProcurementScope.Both || s.PlanningMethods.Count > 0;
        if (!restricted) return null;

        bool In(List<string> list, string? v) => v is not null && list.Contains(v, StringComparer.OrdinalIgnoreCase);

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in data.Items)
        {
            if (s.ItemGroups.Count > 0 && !In(s.ItemGroups, i.GroupCode)) continue;
            if (s.Suppliers.Count > 0 && !(i.Procurement == ProcurementType.Buy && In(s.Suppliers, i.PreferredVendor))) continue;
            if (s.ItemCodes.Count > 0 && !In(s.ItemCodes, i.ItemCode)) continue;
            if (s.Procurement == ProcurementScope.Buy && i.Procurement != ProcurementType.Buy) continue;
            if (s.Procurement == ProcurementScope.Make && i.Procurement != ProcurementType.Make) continue;
            if (s.PlanningMethods.Count > 0 && !s.PlanningMethods.Contains(i.PlanningMethod)) continue;
            if (s.ItemSearch.Length > 0 &&
                !i.ItemCode.Contains(s.ItemSearch, StringComparison.OrdinalIgnoreCase) &&
                !(i.ItemName ?? "").Contains(s.ItemSearch, StringComparison.OrdinalIgnoreCase)) continue;
            selected.Add(i.ItemCode);
        }

        if (s.IncludeComponents)
        {
            var kids = data.Bom.GroupBy(b => b.ParentItemCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(b => b.ComponentItemCode).ToArray(), StringComparer.OrdinalIgnoreCase);
            var makeItems = data.Items.Where(i => i.Procurement == ProcurementType.Make)
                .Select(i => i.ItemCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>(selected.Where(makeItems.Contains));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                if (!seen.Add(p) || !kids.TryGetValue(p, out var cs)) continue;
                foreach (var ch in cs) { selected.Add(ch); stack.Push(ch); }
            }
        }
        return selected;
    }

    private static PlanItem CloneItem(PlanItem s) => new()
    {
        ItemCode = s.ItemCode, ItemName = s.ItemName, Procurement = s.Procurement, PlanningMethod = s.PlanningMethod,
        LotSizing = s.LotSizing, LeadTimeDays = s.LeadTimeDays, SafetyStock = s.SafetyStock, ReorderPoint = s.ReorderPoint,
        MinOrderQty = s.MinOrderQty, MaxOrderQty = s.MaxOrderQty, MaxStockLevel = s.MaxStockLevel,
        OrderMultiple = s.OrderMultiple, FixedLotSize = s.FixedLotSize, PeriodOfSupplyDays = s.PeriodOfSupplyDays,
        HomeWarehouse = s.HomeWarehouse, PreferredVendor = s.PreferredVendor, GroupCode = s.GroupCode,
        GroupLeadTimeDays = s.GroupLeadTimeDays, LeadTimeSource = s.LeadTimeSource, SupplierSource = s.SupplierSource,
    };
}
