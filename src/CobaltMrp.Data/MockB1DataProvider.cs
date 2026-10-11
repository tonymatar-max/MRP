using CobaltMrp.Domain;

namespace CobaltMrp.Data;

/// <summary>
/// A self-contained, deterministic <see cref="IB1DataProvider"/> that returns a realistic
/// small make-to-stock manufacturer (a bicycle with a 3-level BOM). It is the backbone of the
/// local demo and is deliberately shaped to exercise every engine path: safety stock, a
/// reorder-point item, a min/max item, each lot-sizing rule, and an item whose lead time pushes
/// the release date into the past so a <see cref="ExceptionType.ReleaseInPast"/> exception fires.
/// </summary>
/// <remarks>
/// Due/order dates are anchored on a supplied "today" (default: <see cref="DateTime.Today"/>) so
/// past/future relationships hold whenever the demo is run, while the structure stays fixed.
/// The same dataset is mirrored by <c>sql/mock-b1-schema.sql</c> so the SQL provider can be
/// tested against a throwaway database and produce the same plan.
/// </remarks>
public sealed class MockB1DataProvider : IB1DataProvider, IMasterDataProvider
{
    private readonly DateOnly _today;

    /// <summary>Create the provider, optionally anchoring relative dates on a fixed day.</summary>
    public MockB1DataProvider(DateOnly? today = null)
        => _today = today ?? DateOnly.FromDateTime(DateTime.Today);

    /// <inheritdoc />
    public string SourceName => "mock";

    private const string Wh = "WH01";

    /// <inheritdoc />
    public Task<PlanningDataSet> LoadAsync(IReadOnlyCollection<string> warehouses, CancellationToken ct = default)
    {
        var ds = Build();

        // Honour a warehouse filter the same way the SQL provider would.
        if (warehouses is { Count: > 0 })
        {
            var set = new HashSet<string>(warehouses, StringComparer.OrdinalIgnoreCase);
            ds = new PlanningDataSet
            {
                Items = ds.Items,
                Bom = ds.Bom,
                Routings = ds.Routings,
                WorkCenters = ds.WorkCenters,
                Stock = ds.Stock.Where(s => set.Contains(s.WarehouseCode)).ToList(),
                Supply = ds.Supply.Where(s => set.Contains(s.WarehouseCode)).ToList(),
                Demand = ds.Demand.Where(d => set.Contains(d.WarehouseCode)).ToList(),
                Forecasts = ds.Forecasts.Where(f => set.Contains(f.WarehouseCode)).ToList(),
                Calendar = ds.Calendar,
            };
        }

        return Task.FromResult(ds);
    }

    /// <inheritdoc />
    public Task<MasterData> LoadMasterAsync(CancellationToken ct = default)
    {
        var groupNames = new Dictionary<string, string>
            { ["10"] = "Finished goods", ["20"] = "Assemblies", ["30"] = "Components", ["40"] = "Raw materials" };
        var groupLead = new Dictionary<string, int?> { ["30"] = 8, ["40"] = 12 };
        var items = Items();
        return Task.FromResult(new MasterData
        {
            Items = items.Select(i => new MasterItem(i, i.GroupCode,
                i.GroupCode is not null && groupNames.TryGetValue(i.GroupCode, out var n) ? n : null)).ToList(),
            Groups = groupNames.Select(g => new ItemGroupInfo(g.Key, g.Value,
                groupLead.TryGetValue(g.Key, out var lt) ? lt : null, items.Count(i => i.GroupCode == g.Key))).ToList(),
            Suppliers = new[]
            {
                new MasterSupplier("V-CYCLE", "Cycle Components Ltd"), new MasterSupplier("V-STEEL", "Steel Works Co"),
                new MasterSupplier("V-COAT", "Coatings & Paints"), new MasterSupplier("V-RUBBER", "Rubber Industries"),
                new MasterSupplier("V-ALLOY", "Alloy Parts GmbH"),
            },
        });
    }

    private PlanningDataSet Build() => new()
    {
        // Mon-Fri plant that closes for one holiday three weeks out (dates relative to today, like the rest).
        Calendar = new WorkCalendar(new[] { DayOfWeek.Saturday, DayOfWeek.Sunday },
            new[] { new KeyValuePair<DateOnly, string>(DateOnly.FromDateTime(DateTime.Today).AddDays(21), "Plant holiday") }),
        Items = Items(),
        Stock = Stock(),
        Bom = Bom(),
        Routings = Routings(),
        WorkCenters = WorkCenters(),
        Supply = Supply(),
        Demand = Demand(),
        Forecasts = Forecasts(),
    };

    private static List<PlanItem> Items() => new()
    {
        // Demo items that always raise an Expedite and a Defer, so Execute can be tried on a fresh install.
        new PlanItem
        {
            ItemCode = "EXP-01", ItemName = "Chain (urgent customer order)", GroupCode = "30", GroupLeadTimeDays = 8,
            PreferredVendor = "V-ALLOY", Procurement = ProcurementType.Buy, PlanningMethod = PlanningMethod.Mrp,
            LotSizing = LotSizingRule.LotForLot, LeadTimeDays = 2,
        },
        new PlanItem
        {
            ItemCode = "DEF-01", ItemName = "Bell (over-ordered)", GroupCode = "30", GroupLeadTimeDays = 8,
            PreferredVendor = "V-ALLOY", Procurement = ProcurementType.Buy, PlanningMethod = PlanningMethod.Mrp,
            LotSizing = LotSizingRule.LotForLot, LeadTimeDays = 2,
        },
        // Finished good — made, lot-for-lot, short production lead time.
        new PlanItem
        {
            GroupCode = "10", ItemCode = "BIKE-01", ItemName = "Mountain Bike",
            Procurement = ProcurementType.Make, PlanningMethod = PlanningMethod.Mrp,
            LotSizing = LotSizingRule.LotForLot, LeadTimeDays = 3,
            SafetyStock = 5, MinOrderQty = 1, OrderMultiple = 1,
        },
        // Frame — made, fixed lot size (exercises FixedQuantity surplus), scrap on its components.
        new PlanItem
        {
            GroupCode = "20", ItemCode = "FRM-01", ItemName = "Bike Frame",
            Procurement = ProcurementType.Make, PlanningMethod = PlanningMethod.Mrp,
            LotSizing = LotSizingRule.FixedQuantity, FixedLotSize = 50, LeadTimeDays = 5,
            SafetyStock = 10, MinOrderQty = 1, OrderMultiple = 1,
        },
        // Wheel — made, period-of-supply (accumulate 14 days of demand into one order).
        new PlanItem
        {
            GroupCode = "20", ItemCode = "WHL-01", ItemName = "Wheel Assembly",
            Procurement = ProcurementType.Make, PlanningMethod = PlanningMethod.Mrp,
            LotSizing = LotSizingRule.PeriodOfSupply, PeriodOfSupplyDays = 14, LeadTimeDays = 4,
            SafetyStock = 20, MinOrderQty = 1, OrderMultiple = 1,
        },
        // Handlebar — bought, min/max multiple lot sizing.
        new PlanItem
        {
            PreferredVendor = "V-CYCLE", GroupCode = "30", GroupLeadTimeDays = 8, ItemCode = "HBR-01", ItemName = "Handlebar",
            Procurement = ProcurementType.Buy, PlanningMethod = PlanningMethod.Mrp,
            LotSizing = LotSizingRule.MinMaxMultiple, MinOrderQty = 25, MaxOrderQty = 500,
            OrderMultiple = 25, LeadTimeDays = 10, SafetyStock = 15,
        },
        // Tubing — bought, REORDER-POINT planned item (exercises ReorderPoint path).
        new PlanItem
        {
            PreferredVendor = "V-STEEL", GroupCode = "40", GroupLeadTimeDays = 12, ItemCode = "TUB-01", ItemName = "Steel Tubing (m)",
            Procurement = ProcurementType.Buy, PlanningMethod = PlanningMethod.ReorderPoint,
            LotSizing = LotSizingRule.FixedQuantity, FixedLotSize = 500,
            ReorderPoint = 200, LeadTimeDays = 7, MinOrderQty = 100, OrderMultiple = 1,
        },
        // Paint — bought, MIN/MAX planned item (exercises MinMax path).
        new PlanItem
        {
            GroupCode = "40", GroupLeadTimeDays = 12, ItemCode = "PNT-01", ItemName = "Paint (litre)",
            Procurement = ProcurementType.Buy, PlanningMethod = PlanningMethod.MinMax,
            LotSizing = LotSizingRule.MinMaxMultiple, MinOrderQty = 20, MaxOrderQty = 200,
            OrderMultiple = 20, ReorderPoint = 30, LeadTimeDays = 5,
        },
        // Rim — bought, lot-for-lot.
        new PlanItem
        {
            PreferredVendor = "V-CYCLE", GroupCode = "30", GroupLeadTimeDays = 8, ItemCode = "RIM-01", ItemName = "Rim",
            Procurement = ProcurementType.Buy, PlanningMethod = PlanningMethod.Mrp,
            LotSizing = LotSizingRule.LotForLot, LeadTimeDays = 8, SafetyStock = 30,
            MinOrderQty = 1, OrderMultiple = 1,
        },
        // Spoke — bought, big order multiple (consumed x32 per wheel).
        new PlanItem
        {
            PreferredVendor = "V-STEEL", GroupCode = "30", GroupLeadTimeDays = 8, ItemCode = "SPK-01", ItemName = "Spoke",
            Procurement = ProcurementType.Buy, PlanningMethod = PlanningMethod.Mrp,
            LotSizing = LotSizingRule.MinMaxMultiple, MinOrderQty = 500, MaxOrderQty = 20000,
            OrderMultiple = 500, LeadTimeDays = 6, SafetyStock = 1000,
        },
        // Tyre — bought, LONG lead time (60d) with near-term demand -> ReleaseInPast exception.
        new PlanItem
        {
            GroupCode = "30", GroupLeadTimeDays = 8, ItemCode = "TYR-01", ItemName = "Tyre",
            Procurement = ProcurementType.Buy, PlanningMethod = PlanningMethod.Mrp,
            LotSizing = LotSizingRule.FixedQuantity, FixedLotSize = 200, LeadTimeDays = 60,
            SafetyStock = 40, MinOrderQty = 1, OrderMultiple = 1,
        },
    };

    private static List<WarehouseStock> Stock() => new()
    {
        new WarehouseStock { ItemCode = "EXP-01", WarehouseCode = Wh, OnHand = 0, Ordered = 50 },
        new WarehouseStock { ItemCode = "DEF-01", WarehouseCode = Wh, OnHand = 500, Ordered = 50 },
        new WarehouseStock { ItemCode = "BIKE-01", WarehouseCode = Wh, OnHand = 8,    Committed = 10, Ordered = 0 },
        new WarehouseStock { ItemCode = "FRM-01",  WarehouseCode = Wh, OnHand = 12,   Committed = 0,  Ordered = 50 },
        new WarehouseStock { ItemCode = "WHL-01",  WarehouseCode = Wh, OnHand = 30,   Committed = 0,  Ordered = 0 },
        new WarehouseStock { ItemCode = "HBR-01",  WarehouseCode = Wh, OnHand = 40,   Committed = 0,  Ordered = 0 },
        new WarehouseStock { ItemCode = "TUB-01",  WarehouseCode = Wh, OnHand = 150,  Committed = 0,  Ordered = 0 }, // below ReorderPoint 200
        new WarehouseStock { ItemCode = "PNT-01",  WarehouseCode = Wh, OnHand = 15,   Committed = 0,  Ordered = 0 }, // below MinMax reorder 30
        new WarehouseStock { ItemCode = "RIM-01",  WarehouseCode = Wh, OnHand = 60,   Committed = 0,  Ordered = 0 },
        new WarehouseStock { ItemCode = "SPK-01",  WarehouseCode = Wh, OnHand = 2500, Committed = 0,  Ordered = 0 },
        new WarehouseStock { ItemCode = "TYR-01",  WarehouseCode = Wh, OnHand = 25,   Committed = 0,  Ordered = 0 },
        // Second warehouse holding spare stock, so warehouse-level planning can cover shortages with transfers.
        new WarehouseStock { ItemCode = "HBR-01",  WarehouseCode = "WH02", OnHand = 200 },
        new WarehouseStock { ItemCode = "RIM-01",  WarehouseCode = "WH02", OnHand = 150 },
        new WarehouseStock { ItemCode = "SPK-01",  WarehouseCode = "WH02", OnHand = 5000 },
        new WarehouseStock { ItemCode = "TUB-01",  WarehouseCode = "WH02", OnHand = 400 },
    };

    private static List<BomComponent> Bom() => new()
    {
        // Level 0: Bicycle
        new BomComponent { ParentItemCode = "BIKE-01", ComponentItemCode = "FRM-01", QuantityPer = 1, WarehouseCode = Wh },
        new BomComponent { ParentItemCode = "BIKE-01", ComponentItemCode = "WHL-01", QuantityPer = 2, WarehouseCode = Wh },
        new BomComponent { ParentItemCode = "BIKE-01", ComponentItemCode = "HBR-01", QuantityPer = 1, WarehouseCode = Wh },
        // Level 1: Frame
        new BomComponent { ParentItemCode = "FRM-01", ComponentItemCode = "TUB-01", QuantityPer = 3, ScrapFactor = 0.05m, WarehouseCode = Wh },
        new BomComponent { ParentItemCode = "FRM-01", ComponentItemCode = "PNT-01", QuantityPer = 0.5m, WarehouseCode = Wh },
        // Level 1: Wheel
        new BomComponent { ParentItemCode = "WHL-01", ComponentItemCode = "RIM-01", QuantityPer = 1, WarehouseCode = Wh },
        new BomComponent { ParentItemCode = "WHL-01", ComponentItemCode = "SPK-01", QuantityPer = 32, ScrapFactor = 0.02m, WarehouseCode = Wh },
        new BomComponent { ParentItemCode = "WHL-01", ComponentItemCode = "TYR-01", QuantityPer = 1, WarehouseCode = Wh },
    };

    private static List<RoutingOperation> Routings() => new()
    {
        // Bicycle final assembly.
        new RoutingOperation { ItemCode = "BIKE-01", Sequence = 10, WorkCenterCode = "ASSY", HoursPerUnit = 0.75m, SetupHours = 0.5m },
        new RoutingOperation { ItemCode = "BIKE-01", Sequence = 20, WorkCenterCode = "QC",   HoursPerUnit = 0.20m, SetupHours = 0m },
        // Frame fabrication + paint.
        new RoutingOperation { ItemCode = "FRM-01", Sequence = 10, WorkCenterCode = "WELD", HoursPerUnit = 0.50m, SetupHours = 1.0m },
        new RoutingOperation { ItemCode = "FRM-01", Sequence = 20, WorkCenterCode = "PAINT", HoursPerUnit = 0.30m, SetupHours = 0.5m },
        // Wheel build.
        new RoutingOperation { ItemCode = "WHL-01", Sequence = 10, WorkCenterCode = "ASSY", HoursPerUnit = 0.40m, SetupHours = 0.25m },
    };

    private static List<WorkCenter> WorkCenters() => new()
    {
        new WorkCenter { Code = "ASSY",  Name = "Final Assembly", DailyCapacityHours = 16, EfficiencyFactor = 0.9m },
        new WorkCenter { Code = "WELD",  Name = "Frame Welding",  DailyCapacityHours = 8,  EfficiencyFactor = 0.85m },
        new WorkCenter { Code = "PAINT", Name = "Paint Booth",    DailyCapacityHours = 8,  EfficiencyFactor = 1.0m },
        new WorkCenter { Code = "QC",    Name = "Quality Control",DailyCapacityHours = 8,  EfficiencyFactor = 1.0m },
    };

    private List<SupplyOrder> Supply() => new()
    {
        // Open production order for frames (mirrors OnOrder=50 on OITW).
        new SupplyOrder { ItemCode = "FRM-01", WarehouseCode = Wh, Type = SupplyType.ProductionOrder,
            Quantity = 50, DueDate = _today.AddDays(6), SourceDocKey = "OWOR:3301" },
        // EXP-01: the customer wants 20 on day 5 but the open purchase order only arrives on day 20 (raises Expedite).
        new SupplyOrder { ItemCode = "EXP-01", WarehouseCode = Wh, Type = SupplyType.PurchaseOrder,
            Quantity = 50, DueDate = _today.AddDays(20), SourceDocKey = "OPOR:4601", SourceLine = 0 },
        // DEF-01: plenty in stock and another 50 on order with no demand (raises Defer).
        new SupplyOrder { ItemCode = "DEF-01", WarehouseCode = Wh, Type = SupplyType.PurchaseOrder,
            Quantity = 50, DueDate = _today.AddDays(20), SourceDocKey = "OPOR:4602", SourceLine = 0 },
        // Open PO for rims arriving comfortably.
        new SupplyOrder { ItemCode = "RIM-01", WarehouseCode = Wh, Type = SupplyType.PurchaseOrder,
            Quantity = 100, DueDate = _today.AddDays(9), SourceDocKey = "OPOR:4501", SourceLine = 0 },
        // Open PO for tyres already in the past (exercises PastDue on supply).
        new SupplyOrder { ItemCode = "TYR-01", WarehouseCode = Wh, Type = SupplyType.PurchaseOrder,
            Quantity = 200, DueDate = _today.AddDays(-2), SourceDocKey = "OPOR:4502", SourceLine = 0 },
    };

    private List<DemandOrder> Demand() => new()
    {
        // Two open sales orders for bikes; the near one drives the whole BOM explosion.
        new DemandOrder { ItemCode = "BIKE-01", WarehouseCode = Wh, Source = DemandSource.SalesOrder,
            Quantity = 20, DueDate = _today.AddDays(15), SourceDocKey = "ORDR:1001" },
        new DemandOrder { ItemCode = "BIKE-01", WarehouseCode = Wh, Source = DemandSource.SalesOrder,
            Quantity = 30, DueDate = _today.AddDays(40), SourceDocKey = "ORDR:1002" },
        new DemandOrder { ItemCode = "EXP-01", WarehouseCode = Wh, Source = DemandSource.SalesOrder,
            Quantity = 20, DueDate = _today.AddDays(5), SourceDocKey = "ORDR:1004" },
        // Components still to be issued to the open frame production order OWOR:3301 (what B1's MRP plans as demand).
        new DemandOrder { ItemCode = "TUB-01", WarehouseCode = Wh, Source = DemandSource.DependentDemand,
            Quantity = 60, DueDate = _today.AddDays(2), SourceDocKey = "OWOR:3301" },
        new DemandOrder { ItemCode = "PNT-01", WarehouseCode = Wh, Source = DemandSource.DependentDemand,
            Quantity = 12, DueDate = _today.AddDays(2), SourceDocKey = "OWOR:3301" },
        // A spare-part sales order for a purchased component.
        new DemandOrder { ItemCode = "HBR-01", WarehouseCode = Wh, Source = DemandSource.SalesOrder,
            Quantity = 10, DueDate = _today.AddDays(12), SourceDocKey = "ORDR:1003" },
    };

    private List<Forecast> Forecasts() => new()
    {
        // Monthly bike forecast, partially consumed by the sales orders above.
        new Forecast { ItemCode = "BIKE-01", WarehouseCode = Wh, PeriodStart = _today.AddDays(0),  Quantity = 40, Consumed = 20 },
        new Forecast { ItemCode = "BIKE-01", WarehouseCode = Wh, PeriodStart = _today.AddDays(30), Quantity = 40, Consumed = 30 },
        new Forecast { ItemCode = "BIKE-01", WarehouseCode = Wh, PeriodStart = _today.AddDays(60), Quantity = 50, Consumed = 0 },
    };
}
