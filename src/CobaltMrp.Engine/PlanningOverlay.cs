using CobaltMrp.Domain;

namespace CobaltMrp.Engine;

/// <summary>
/// Layers the planner's Cobalt-side changes (item overrides, supplier lead times) over what SAP B1 holds, picks each
/// item's supplier, and applies the lead-time rule. Pure: it returns new items and never touches the input.
/// </summary>
public static class PlanningOverlay
{
    public const string SrcMasterData = "master-data";
    public const string SrcB1 = "b1-preferred";

    public static PlanningDataSet Apply(
        PlanningDataSet data,
        IReadOnlyDictionary<string, ItemOverride> itemOverrides,
        IReadOnlyDictionary<string, SupplierSetting> supplierSettings,
        LeadTimeCriteria leadTime)
    {
        var items = data.Items.Select(i => ApplyToItem(i,
            itemOverrides.TryGetValue(i.ItemCode, out var o) ? o : null, supplierSettings, leadTime)).ToList();

        return new PlanningDataSet
        {
            Items = items, Stock = data.Stock, Bom = data.Bom, Routings = data.Routings, WorkCenters = data.WorkCenters,
            Supply = data.Supply, Demand = data.Demand, Forecasts = data.Forecasts, Calendar = data.Calendar,
        };
    }

    public static PlanItem ApplyToItem(PlanItem b1, ItemOverride? o,
        IReadOnlyDictionary<string, SupplierSetting> supplierSettings, LeadTimeCriteria leadTime)
    {
        var i = Clone(b1);

        if (o is not null)
        {
            if (o.PlanningMethod is { } pm) i.PlanningMethod = pm;
            if (o.LotSizing is { } ls) i.LotSizing = ls;
            if (o.MinOrderQty is { } mo) i.MinOrderQty = mo;
            if (o.OrderMultiple is { } om) i.OrderMultiple = om;
            if (o.SafetyStock is { } ss) i.SafetyStock = ss;
            if (o.ReorderPoint is { } rp) i.ReorderPoint = rp;
            if (o.MaxStockLevel is { } ml) i.MaxStockLevel = ml;
            if (o.FixedLotSize is { } fl) i.FixedLotSize = fl;
            if (o.PeriodOfSupplyDays is { } pd) i.PeriodOfSupplyDays = pd;
            // Min/max planning uses ReorderPoint as the minimum level: keep it in step with an edited safety stock.
            if (i.PlanningMethod == PlanningMethod.MinMax && o.ReorderPoint is null && o.SafetyStock is not null)
                i.ReorderPoint = i.SafetyStock;
        }

        // Supplier: the planner's default supplier wins over SAP B1's preferred vendor.
        var overrideSupplier = string.IsNullOrWhiteSpace(o?.Supplier) ? null : o!.Supplier!.Trim();
        if (overrideSupplier is not null) { i.PreferredVendor = overrideSupplier; i.SupplierSource = SrcMasterData; }
        else if (!string.IsNullOrWhiteSpace(b1.PreferredVendor)) { i.PreferredVendor = b1.PreferredVendor!.Trim(); i.SupplierSource = SrcB1; }
        else { i.PreferredVendor = null; i.SupplierSource = null; }

        // Lead time.
        int? itemDays = o?.LeadTimeDays ?? (b1.LeadTimeDays > 0 ? b1.LeadTimeDays : null);
        int? groupDays = b1.GroupLeadTimeDays is > 0 ? b1.GroupLeadTimeDays : null;
        int? supplierDays = null;
        var isBuy = b1.Procurement == ProcurementType.Buy;
        if (isBuy && i.PreferredVendor is not null && supplierSettings.TryGetValue(i.PreferredVendor, out var s))
            supplierDays = s.LeadTimeDays;

        var (days, source) = ResolveLeadTime(isBuy, supplierDays, itemDays, groupDays, leadTime);
        i.LeadTimeDays = days;
        i.LeadTimeSource = source;
        return i;
    }

    /// <summary>
    /// Applies the lead-time rule. <c>FirstAvailable</c> takes the first source in the priority order that has a
    /// value; <c>Longest</c> takes the largest of the sources that do. Make items have no supplier. When nothing has
    /// a value the default is used (source "Default", or "None" when the default is 0).
    /// </summary>
    public static (int Days, string Source) ResolveLeadTime(bool isBuy, int? supplierDays, int? itemDays, int? groupDays, LeadTimeCriteria c)
    {
        int? Value(LeadTimeSourceKind k) => k switch
        {
            LeadTimeSourceKind.Supplier => isBuy ? supplierDays : null,
            LeadTimeSourceKind.Item => itemDays,
            _ => groupDays,
        };

        var available = c.Priority.Select(k => (Kind: k, Days: Value(k))).Where(x => x.Days is not null).ToList();
        if (available.Count > 0)
        {
            var pick = c.Mode == LeadTimeMode.Longest
                ? available.OrderByDescending(x => x.Days).First()      // ties keep the higher-priority source
                : available[0];
            return (pick.Days!.Value, pick.Kind.ToString());
        }
        return c.DefaultDays > 0 ? (c.DefaultDays, "Default") : (0, "None");
    }

    private static PlanItem Clone(PlanItem s) => new()
    {
        ItemCode = s.ItemCode, ItemName = s.ItemName, Procurement = s.Procurement, PlanningMethod = s.PlanningMethod,
        LotSizing = s.LotSizing, LeadTimeDays = s.LeadTimeDays, SafetyStock = s.SafetyStock, ReorderPoint = s.ReorderPoint,
        MinOrderQty = s.MinOrderQty, MaxOrderQty = s.MaxOrderQty, MaxStockLevel = s.MaxStockLevel,
        OrderMultiple = s.OrderMultiple, FixedLotSize = s.FixedLotSize, PeriodOfSupplyDays = s.PeriodOfSupplyDays,
        HomeWarehouse = s.HomeWarehouse, PreferredVendor = s.PreferredVendor, GroupCode = s.GroupCode,
        GroupLeadTimeDays = s.GroupLeadTimeDays, LeadTimeSource = s.LeadTimeSource, SupplierSource = s.SupplierSource,
    };
}
