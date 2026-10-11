using CobaltMrp.Domain;
using CobaltMrp.Engine;
using Xunit;

namespace CobaltMrp.Engine.Tests;

/// <summary>Lead-time rules, master-data overrides, wizard criteria and output scoping.</summary>
public class WizardCriteriaTests
{
    private static readonly DateOnly Mon = new(2026, 10, 12);
    private static readonly IReadOnlyDictionary<string, SupplierSetting> NoSuppliers = new Dictionary<string, SupplierSetting>();
    private static readonly IReadOnlyDictionary<string, ItemOverride> NoOverrides = new Dictionary<string, ItemOverride>();

    private static LeadTimeCriteria Rule(LeadTimeMode mode, params LeadTimeSourceKind[] order) =>
        new() { Mode = mode, Priority = order.Length == 0
            ? new() { LeadTimeSourceKind.Supplier, LeadTimeSourceKind.Item, LeadTimeSourceKind.ItemGroup }
            : order.ToList() };

    private static PlanItem Buy(string code, int itemLt = 0, int? groupLt = null, string? vendor = null, Action<PlanItem>? cfg = null)
    {
        var i = new PlanItem { ItemCode = code, ItemName = code + " name", Procurement = ProcurementType.Buy,
                               LeadTimeDays = itemLt, GroupLeadTimeDays = groupLt, PreferredVendor = vendor, GroupCode = "100" };
        cfg?.Invoke(i); return i;
    }

    private static Dictionary<string, SupplierSetting> Sup(string code, int? days) =>
        new() { [code] = new SupplierSetting { CardCode = code, LeadTimeDays = days } };

    // ---- Lead time resolution ---------------------------------------------------------------------------

    [Theory]
    [InlineData(LeadTimeSourceKind.Supplier, LeadTimeSourceKind.Item, LeadTimeSourceKind.ItemGroup, 21, "Supplier")]
    [InlineData(LeadTimeSourceKind.Item, LeadTimeSourceKind.Supplier, LeadTimeSourceKind.ItemGroup, 7, "Item")]
    [InlineData(LeadTimeSourceKind.ItemGroup, LeadTimeSourceKind.Item, LeadTimeSourceKind.Supplier, 5, "ItemGroup")]
    public void LeadTime_FirstAvailable_FollowsThePriorityOrder(LeadTimeSourceKind a, LeadTimeSourceKind b, LeadTimeSourceKind c, int days, string source)
    {
        var r = PlanningOverlay.ResolveLeadTime(true, 21, 7, 5, Rule(LeadTimeMode.FirstAvailable, a, b, c));
        Assert.Equal((days, source), r);
    }

    [Fact]
    public void LeadTime_Longest_TakesTheLargestAvailable()
    {
        Assert.Equal((21, "Supplier"), PlanningOverlay.ResolveLeadTime(true, 21, 7, 5, Rule(LeadTimeMode.Longest)));
        Assert.Equal((7, "Item"), PlanningOverlay.ResolveLeadTime(true, null, 7, 5, Rule(LeadTimeMode.Longest)));
    }

    [Fact]
    public void LeadTime_MissingFirstChoice_FallsThroughToTheNext()
    {
        Assert.Equal((7, "Item"), PlanningOverlay.ResolveLeadTime(true, null, 7, 5, Rule(LeadTimeMode.FirstAvailable)));
        Assert.Equal((5, "ItemGroup"), PlanningOverlay.ResolveLeadTime(true, null, null, 5, Rule(LeadTimeMode.FirstAvailable)));
    }

    [Fact]
    public void LeadTime_MakeItemsHaveNoSupplier()
    {
        Assert.Equal((7, "Item"), PlanningOverlay.ResolveLeadTime(false, 21, 7, 5, Rule(LeadTimeMode.FirstAvailable)));
    }

    [Fact]
    public void LeadTime_NothingKnown_UsesTheDefaultOrNone()
    {
        var withDefault = new LeadTimeCriteria { DefaultDays = 10 };
        Assert.Equal((10, "Default"), PlanningOverlay.ResolveLeadTime(true, null, null, null, withDefault));
        Assert.Equal((0, "None"), PlanningOverlay.ResolveLeadTime(true, null, null, null, new LeadTimeCriteria()));
    }

    [Fact]
    public void LeadTime_AnExplicitZeroOverrideCountsAsAValue()
    {
        // A planner who sets lead time to 0 on purpose ("always in stock") must beat SAP B1's 14.
        var o = new ItemOverride { ItemCode = "X", LeadTimeDays = 0 };
        var i = PlanningOverlay.ApplyToItem(Buy("X", itemLt: 14), o, NoSuppliers, Rule(LeadTimeMode.FirstAvailable, LeadTimeSourceKind.Item));
        Assert.Equal(0, i.LeadTimeDays);
        Assert.Equal("Item", i.LeadTimeSource);
    }

    [Fact]
    public void LeadTime_B1ZeroMeansNotSet()
    {
        var i = PlanningOverlay.ApplyToItem(Buy("X", itemLt: 0, groupLt: 5), null, NoSuppliers, Rule(LeadTimeMode.FirstAvailable, LeadTimeSourceKind.Item, LeadTimeSourceKind.ItemGroup));
        Assert.Equal((5, "ItemGroup"), (i.LeadTimeDays, i.LeadTimeSource));
    }

    // ---- Overlay: overrides and supplier ----------------------------------------------------------------

    [Fact]
    public void Overlay_OverrideSupplierBeatsB1_AndSourceSaysSo()
    {
        var o = new ItemOverride { ItemCode = "X", Supplier = "V-NEW" };
        var i = PlanningOverlay.ApplyToItem(Buy("X", vendor: "V-B1"), o, NoSuppliers, new LeadTimeCriteria());
        Assert.Equal(("V-NEW", "master-data"), (i.PreferredVendor, i.SupplierSource));
        var j = PlanningOverlay.ApplyToItem(Buy("X", vendor: "V-B1"), null, NoSuppliers, new LeadTimeCriteria());
        Assert.Equal(("V-B1", "b1-preferred"), (j.PreferredVendor, j.SupplierSource));
        var k = PlanningOverlay.ApplyToItem(Buy("X"), null, NoSuppliers, new LeadTimeCriteria());
        Assert.Null(k.PreferredVendor); Assert.Null(k.SupplierSource);
    }

    [Fact]
    public void Overlay_SupplierLeadTimeFollowsTheEffectiveSupplier()
    {
        // The override changes the supplier, so the supplier lead time that applies changes with it.
        var sup = new Dictionary<string, SupplierSetting>
        {
            ["V-B1"] = new() { CardCode = "V-B1", LeadTimeDays = 30 },
            ["V-NEW"] = new() { CardCode = "V-NEW", LeadTimeDays = 4 },
        };
        var rule = Rule(LeadTimeMode.FirstAvailable);
        Assert.Equal(30, PlanningOverlay.ApplyToItem(Buy("X", 9, vendor: "V-B1"), null, sup, rule).LeadTimeDays);
        Assert.Equal(4, PlanningOverlay.ApplyToItem(Buy("X", 9, vendor: "V-B1"), new ItemOverride { ItemCode = "X", Supplier = "V-NEW" }, sup, rule).LeadTimeDays);
    }

    [Fact]
    public void Overlay_AppliesPlanningOverrides_AndDoesNotMutateTheInput()
    {
        var b1 = Buy("X", cfg: i => { i.MinOrderQty = 10; i.SafetyStock = 5; });
        var o = new ItemOverride { ItemCode = "X", MinOrderQty = 50, OrderMultiple = 25, SafetyStock = 8, PlanningMethod = PlanningMethod.MinMax, MaxStockLevel = 200 };
        var e = PlanningOverlay.ApplyToItem(b1, o, NoSuppliers, new LeadTimeCriteria());
        Assert.Equal((50m, 25m, 8m, 200m), (e.MinOrderQty, e.OrderMultiple, e.SafetyStock, e.MaxStockLevel));
        Assert.Equal(8m, e.ReorderPoint);                       // min/max floor follows the edited safety stock
        Assert.Equal((10m, 5m), (b1.MinOrderQty, b1.SafetyStock));   // SAP B1 data untouched
    }

    [Fact]
    public void Overlay_DrivesThePlan_ReleaseDateUsesTheResolvedLeadTime()
    {
        PlanningDataSet Data() => new()
        {
            Items = new[] { Buy("X", itemLt: 5, vendor: "V1") },
            Stock = new[] { new WarehouseStock { ItemCode = "X", WarehouseCode = "01", OnHand = 0 } },
            Demand = new[] { new DemandOrder { ItemCode = "X", WarehouseCode = "01", Source = DemandSource.SalesOrder, Quantity = 5, DueDate = Mon.AddDays(40), SourceDocKey = "SO" } },
        };
        var opt = new MrpOptions { HorizonStart = Mon, HorizonDays = 90, BucketDays = 1 };
        var sup = Sup("V1", 20);

        var supplierFirst = PlanningOverlay.Apply(Data(), NoOverrides, sup, Rule(LeadTimeMode.FirstAvailable));
        var po = Assert.Single(new MrpEngine().Run(supplierFirst, opt).PlannedOrders);
        Assert.Equal(Mon.AddDays(20), po.ReleaseDate);
        Assert.Equal(("Supplier", 20, "V1", "b1-preferred"), (po.LeadTimeSource, po.LeadTimeDays, po.VendorCode, po.VendorSource));

        var itemFirst = PlanningOverlay.Apply(Data(), NoOverrides, sup, Rule(LeadTimeMode.FirstAvailable, LeadTimeSourceKind.Item, LeadTimeSourceKind.Supplier));
        var po2 = Assert.Single(new MrpEngine().Run(itemFirst, opt).PlannedOrders);
        Assert.Equal(Mon.AddDays(35), po2.ReleaseDate);
        Assert.Equal("Item", po2.LeadTimeSource);
    }

    // ---- Criteria: input filters ------------------------------------------------------------------------

    private static PlanningDataSet Rich() => new()
    {
        Items = new[]
        {
            Buy("MRPITEM", cfg: i => { i.SafetyStock = 10; i.MinOrderQty = 100; i.OrderMultiple = 25; }),
            Buy("MINMAX", cfg: i => { i.PlanningMethod = PlanningMethod.MinMax; i.SafetyStock = 10; i.ReorderPoint = 10; i.MaxStockLevel = 50; }),
        },
        Stock = new[]
        {
            new WarehouseStock { ItemCode = "MRPITEM", WarehouseCode = "01", OnHand = 40 },
            new WarehouseStock { ItemCode = "MINMAX", WarehouseCode = "01", OnHand = 40 },
        },
        Demand = new[]
        {
            new DemandOrder { ItemCode = "MRPITEM", WarehouseCode = "01", Source = DemandSource.SalesOrder, Quantity = 1, DueDate = Mon.AddDays(-5), SourceDocKey = "OLD" },
            new DemandOrder { ItemCode = "MRPITEM", WarehouseCode = "01", Source = DemandSource.SalesOrder, Quantity = 2, DueDate = Mon.AddDays(10), SourceDocKey = "IN" },
            new DemandOrder { ItemCode = "MRPITEM", WarehouseCode = "01", Source = DemandSource.SalesOrder, Quantity = 4, DueDate = Mon.AddDays(50), SourceDocKey = "LATE" },
            new DemandOrder { ItemCode = "MRPITEM", WarehouseCode = "01", Source = DemandSource.DependentDemand, Quantity = 8, DueDate = Mon.AddDays(12), SourceDocKey = "OWOR:1" },
        },
        Supply = new[]
        {
            new SupplyOrder { ItemCode = "MRPITEM", WarehouseCode = "01", Type = SupplyType.PurchaseOrder, Quantity = 3, DueDate = Mon.AddDays(5), SourceDocKey = "OPOR:1" },
            new SupplyOrder { ItemCode = "MRPITEM", WarehouseCode = "01", Type = SupplyType.ProductionOrder, Quantity = 6, DueDate = Mon.AddDays(6), SourceDocKey = "OWOR:2" },
        },
        Forecasts = new[] { new Forecast { ItemCode = "MRPITEM", WarehouseCode = "01", PeriodStart = Mon.AddDays(20), Quantity = 9 } },
    };

    private static PlanningDataSet Prep(Action<RunCriteria> edit) { var c = new RunCriteria(); edit(c); return CriteriaApplier.PrepareData(Rich(), c, Mon); }

    [Fact]
    public void Criteria_Defaults_ChangeNothing()
    {
        var d = Prep(_ => { });
        Assert.Equal((4, 2, 1, 2), (d.Demand.Count, d.Supply.Count, d.Forecasts.Count, d.Items.Count));
        Assert.Equal(40m, d.Stock.First().OnHand);
    }

    [Fact]
    public void Criteria_SalesOrdersOff_KeepsOtherDemandKinds()
    {
        var d = Prep(c => c.Demand.SalesOrders = false);
        Assert.All(d.Demand, x => Assert.NotEqual(DemandSource.SalesOrder, x.Source));
        Assert.Single(d.Demand);
    }

    [Fact]
    public void Criteria_SalesOrderDateWindow_OnlyFiltersSalesOrders()
    {
        var d = Prep(c => { c.Demand.SalesOrderFrom = Mon; c.Demand.SalesOrderTo = Mon.AddDays(30); });
        Assert.Equal(new[] { "IN", "OWOR:1" }, d.Demand.Select(x => x.SourceDocKey).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Criteria_PastDueIgnore_DropsOverdueOnly()
    {
        var d = Prep(c => c.Demand.PastDue = PastDueMode.Ignore);
        Assert.DoesNotContain(d.Demand, x => x.SourceDocKey == "OLD");
        Assert.Equal(3, d.Demand.Count);
    }

    [Fact]
    public void Criteria_ProductionComponentsOff_DropsThem()
    {
        var d = Prep(c => c.Demand.ProductionComponents = false);
        Assert.DoesNotContain(d.Demand, x => x.Source == DemandSource.DependentDemand);
    }

    [Fact]
    public void Criteria_SupplyToggles()
    {
        Assert.DoesNotContain(Prep(c => c.Supply.OpenPurchaseOrders = false).Supply, s => s.Type == SupplyType.PurchaseOrder);
        Assert.DoesNotContain(Prep(c => c.Supply.OpenProductionOrders = false).Supply, s => s.Type == SupplyType.ProductionOrder);
        Assert.All(Prep(c => c.Supply.OnHand = false).Stock, s => Assert.Equal(0m, s.OnHand));
        Assert.Empty(Prep(c => c.Demand.Forecasts = false).Forecasts);
    }

    [Fact]
    public void Criteria_SafetyStockOff_OnlyAffectsMrpItems_NotMinMaxPolicy()
    {
        var d = Prep(c => c.Demand.SafetyStock = false);
        Assert.Equal(0m, d.Items.Single(i => i.ItemCode == "MRPITEM").SafetyStock);
        Assert.Equal(10m, d.Items.Single(i => i.ItemCode == "MINMAX").SafetyStock);
    }

    [Fact]
    public void Criteria_LotSizingOff_ClearsMinOrderAndMultiple_WithoutTouchingTheSource()
    {
        var src = Rich();
        var c = new RunCriteria(); c.LotSizing.ApplyMinOrder = false; c.LotSizing.ApplyMultiples = false;
        var d = CriteriaApplier.PrepareData(src, c, Mon);
        Assert.Equal((0m, 0m), (d.Items[0].MinOrderQty, d.Items[0].OrderMultiple));
        Assert.Equal((100m, 25m), (src.Items[0].MinOrderQty, src.Items[0].OrderMultiple));
    }

    [Fact]
    public void Criteria_ToOptions_MapsHorizonAndSwitches()
    {
        var c = new RunCriteria();
        c.Horizon.Days = 90; c.Horizon.BucketDays = 14; c.Demand.Forecasts = false; c.Capacity.Enabled = false;
        c.Demand.ForecastConsumption = ForecastConsumptionMode.None; c.Scope.Warehouses.Add("02");
        var o = CriteriaApplier.ToOptions(c, Mon, "tony", null);
        Assert.Equal((Mon, 90, 14, false, false, false), (o.HorizonStart, o.HorizonDays, o.BucketDays, o.UseForecasts, o.RunCapacityPlanning, o.ConsumeForecast));
        Assert.Equal("02", Assert.Single(o.Warehouses));
        Assert.Null(o.ItemScope);
    }

    // ---- Scope ------------------------------------------------------------------------------------------

    private static PlanningDataSet Tree() => new()
    {
        Items = new[]
        {
            new PlanItem { ItemCode = "BIKE", ItemName = "Mountain bike", Procurement = ProcurementType.Make, GroupCode = "1" },
            new PlanItem { ItemCode = "WHEEL", ItemName = "Wheel", Procurement = ProcurementType.Make, GroupCode = "2" },
            Buy("SPOKE", vendor: "V-STEEL", cfg: i => i.GroupCode = "3"),
            Buy("TYRE", vendor: "V-RUBBER", cfg: i => i.GroupCode = "3"),
            Buy("SEAT", vendor: "V-STEEL", cfg: i => i.GroupCode = "4"),
        },
        Bom = new[]
        {
            new BomComponent { ParentItemCode = "BIKE", ComponentItemCode = "WHEEL", QuantityPer = 2 },
            new BomComponent { ParentItemCode = "BIKE", ComponentItemCode = "SEAT", QuantityPer = 1 },
            new BomComponent { ParentItemCode = "WHEEL", ComponentItemCode = "SPOKE", QuantityPer = 32 },
            new BomComponent { ParentItemCode = "WHEEL", ComponentItemCode = "TYRE", QuantityPer = 1 },
        },
    };

    [Fact]
    public void Scope_NoRestrictions_MeansEverything() => Assert.Null(CriteriaApplier.Scope(Tree(), new RunCriteria()));

    [Fact]
    public void Scope_BySupplier_SelectsThatSuppliersBuyItems()
    {
        var c = new RunCriteria(); c.Scope.Suppliers.Add("v-steel");           // case-insensitive
        Assert.Equal(new[] { "SEAT", "SPOKE" }, CriteriaApplier.Scope(Tree(), c)!.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Scope_ByGroupProcurementSearchAndCodes()
    {
        var c = new RunCriteria(); c.Scope.ItemGroups.Add("3");
        Assert.Equal(new[] { "SPOKE", "TYRE" }, CriteriaApplier.Scope(Tree(), c)!.OrderBy(x => x).ToArray());

        c = new RunCriteria(); c.Scope.Procurement = ProcurementScope.Make; c.Scope.IncludeComponents = false;
        Assert.Equal(new[] { "BIKE", "WHEEL" }, CriteriaApplier.Scope(Tree(), c)!.OrderBy(x => x).ToArray());

        c = new RunCriteria(); c.Scope.ItemSearch = "mountain"; c.Scope.IncludeComponents = false;      // matches the NAME
        Assert.Equal(new[] { "BIKE" }, CriteriaApplier.Scope(Tree(), c)!.ToArray());

        c = new RunCriteria(); c.Scope.ItemCodes.Add("TYRE");
        Assert.Equal(new[] { "TYRE" }, CriteriaApplier.Scope(Tree(), c)!.ToArray());
    }

    [Fact]
    public void Scope_IncludeComponents_WalksTheWholeBomBelowSelectedMakeItems()
    {
        var c = new RunCriteria(); c.Scope.ItemCodes.Add("WHEEL"); c.Scope.IncludeComponents = true;
        Assert.Equal(new[] { "SPOKE", "TYRE", "WHEEL" }, CriteriaApplier.Scope(Tree(), c)!.OrderBy(x => x).ToArray());
        c.Scope.IncludeComponents = false;
        Assert.Equal(new[] { "WHEEL" }, CriteriaApplier.Scope(Tree(), c)!.ToArray());
    }

    [Fact]
    public void Scope_Output_KeepsOnlySelectedItems_ButComponentsStillSeeParentDemand()
    {
        var data = Tree();
        var full = new PlanningDataSet
        {
            Items = data.Items, Bom = data.Bom,
            Stock = data.Items.Select(i => new WarehouseStock { ItemCode = i.ItemCode, WarehouseCode = "01" }).ToList(),
            Demand = new[] { new DemandOrder { ItemCode = "BIKE", WarehouseCode = "01", Source = DemandSource.SalesOrder, Quantity = 10, DueDate = Mon.AddDays(30), SourceDocKey = "SO" } },
        };
        var all = new MrpEngine().Run(full, new MrpOptions { HorizonStart = Mon, HorizonDays = 90, BucketDays = 1 });
        var spokeFull = all.PlannedOrders.Single(o => o.ItemCode == "SPOKE").Quantity;
        Assert.Equal(10 * 2 * 32, spokeFull);

        // Select ONLY the spoke: its demand comes from BIKE -> WHEEL, which are not selected, yet must still drive it.
        var scoped = new MrpEngine().Run(full, new MrpOptions
            { HorizonStart = Mon, HorizonDays = 90, BucketDays = 1, ItemScope = new HashSet<string> { "SPOKE" } });
        var only = Assert.Single(scoped.PlannedOrders);
        Assert.Equal(("SPOKE", spokeFull), (only.ItemCode, only.Quantity));
        Assert.All(scoped.Pegging, p => Assert.Equal(only.Id, p.PlannedOrderId));          // no orphaned pegging
        Assert.DoesNotContain(scoped.Events, e => e.Kind == CalendarEventKind.PlannedRelease && e.ItemCode != "SPOKE");
    }

    // ---- Criteria object --------------------------------------------------------------------------------

    [Fact]
    public void Criteria_RoundTripsThroughJson()
    {
        var c = new RunCriteria { Source = "b1-sql" };
        c.Scope.Suppliers.Add("V1"); c.Scope.Procurement = ProcurementScope.Buy; c.Demand.PastDue = PastDueMode.Ignore;
        c.Horizon.StartDate = Mon; c.LeadTime.Mode = LeadTimeMode.Longest;
        c.LeadTime.Priority = new() { LeadTimeSourceKind.Item, LeadTimeSourceKind.ItemGroup, LeadTimeSourceKind.Supplier };
        var back = RunCriteria.FromJson(c.ToJson())!;
        Assert.Equal(c.ToJson(), back.ToJson());
        Assert.Contains("\"procurement\":\"Buy\"", c.ToJson());          // enums travel as names
        Assert.Null(RunCriteria.FromJson("not json"));
        Assert.Null(RunCriteria.FromJson(null));
    }

    [Theory]
    [InlineData("source", "Source must")]
    [InlineData("days", "Horizon must")]
    [InlineData("bucket", "Bucket size")]
    [InlineData("group", "not a valid SAP B1 group")]
    [InlineData("dates", "before the 'from'")]
    public void Criteria_Validate_RejectsNonsense(string what, string message)
    {
        var c = new RunCriteria();
        switch (what)
        {
            case "source": c.Source = "oracle"; break;
            case "days": c.Horizon.Days = 5000; break;
            case "bucket": c.Horizon.BucketDays = 0; break;
            case "group": c.Scope.ItemGroups.Add("abc"); break;
            case "dates": c.Demand.SalesOrderFrom = Mon; c.Demand.SalesOrderTo = Mon.AddDays(-1); break;
        }
        Assert.Contains(message, c.Validate());
    }

    [Fact]
    public void Criteria_Validate_NormalisesPriorityAndLists()
    {
        var c = new RunCriteria();
        c.LeadTime.Priority = new() { LeadTimeSourceKind.Item, LeadTimeSourceKind.Item };      // duplicate + missing
        c.Scope.Suppliers = new() { " V1 ", "v1", "" };
        Assert.Null(c.Validate());
        Assert.Equal(new[] { LeadTimeSourceKind.Item, LeadTimeSourceKind.Supplier, LeadTimeSourceKind.ItemGroup }, c.LeadTime.Priority);
        Assert.Equal(new[] { "V1" }, c.Scope.Suppliers);
    }
}
