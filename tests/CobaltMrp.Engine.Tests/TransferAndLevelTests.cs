using CobaltMrp.Domain;
using CobaltMrp.Engine;
using Xunit;

namespace CobaltMrp.Engine.Tests;

/// <summary>Warehouse-level transfers, company-level pooling and purchase requests.</summary>
public class TransferAndLevelTests
{
    private static readonly DateOnly Mon = new(2026, 10, 12);

    private static MrpOptions Opt(bool transfers = true, int lt = 1, bool company = false, PurchaseDocument doc = PurchaseDocument.Order,
        params string[] sources) => new()
    {
        HorizonStart = Mon, HorizonDays = 60, BucketDays = 1, TransferEnabled = transfers, TransferLeadDays = lt,
        CompanyLevel = company, PurchaseDocument = doc, TransferSources = sources,
    };

    private static PlanItem Buy(string code = "X", Action<PlanItem>? cfg = null)
    { var i = new PlanItem { ItemCode = code, Procurement = ProcurementType.Buy }; cfg?.Invoke(i); return i; }

    private static WarehouseStock St(string wh, decimal oh, string item = "X") => new() { ItemCode = item, WarehouseCode = wh, OnHand = oh };
    private static DemandOrder Dem(string wh, decimal q, int day, string item = "X") => new()
        { ItemCode = item, WarehouseCode = wh, Source = DemandSource.SalesOrder, Quantity = q, DueDate = Mon.AddDays(day), SourceDocKey = $"SO-{wh}-{day}" };

    private static MrpRun Run(PlanningDataSet d, MrpOptions o) => new MrpEngine().Run(d, o);

    private static PlanningDataSet Data(PlanItem[] items, WarehouseStock[] stock, DemandOrder[] demand, SupplyOrder[]? supply = null,
        BomComponent[]? bom = null, WorkCalendar? cal = null) =>
        new() { Items = items, Stock = stock, Demand = demand, Supply = supply ?? Array.Empty<SupplyOrder>(), Bom = bom ?? Array.Empty<BomComponent>(), Calendar = cal };

    // ---- Transfers --------------------------------------------------------------------------------------------

    [Fact]
    public void Transfer_CoversTheWholeShortage_NoPurchase()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0), St("02", 300) }, new[] { Dem("01", 100, 10) }), Opt());
        var t = Assert.Single(run.PlannedOrders);
        Assert.Equal(PlannedOrderType.TransferRequest, t.Type);
        Assert.Equal(("02", "01", 100m), (t.FromWarehouse, t.WarehouseCode, t.Quantity));
        Assert.Equal(Mon.AddDays(10), t.DueDate);
        Assert.Equal(Mon.AddDays(9), t.ReleaseDate);             // 1 day transfer time
        Assert.Equal("Transfer", t.LeadTimeSource);
    }

    [Fact]
    public void Transfer_PartialSpare_TheRestIsPurchased()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0), St("02", 60) }, new[] { Dem("01", 100, 10) }), Opt());
        Assert.Equal(60m, run.PlannedOrders.Single(o => o.Type == PlannedOrderType.TransferRequest).Quantity);
        var po = run.PlannedOrders.Single(o => o.Type == PlannedOrderType.PurchaseOrder);
        Assert.Equal((40m, "01"), (po.Quantity, po.WarehouseCode));
    }

    [Fact]
    public void Transfer_RespectsTheSourcesSafetyStock()
    {
        var run = Run(Data(new[] { Buy("X", i => i.SafetyStock = 50) }, new[] { St("01", 50), St("02", 120) }, new[] { Dem("01", 100, 10) }), Opt());
        // 01 must keep 50 itself: it needs 100 + 50 = 150 against 50 on hand = 100 short; 02 can spare 120 - 50 = 70.
        Assert.Equal(70m, run.PlannedOrders.Single(o => o.Type == PlannedOrderType.TransferRequest).Quantity);
        Assert.Equal(30m, run.PlannedOrders.Single(o => o.Type == PlannedOrderType.PurchaseOrder).Quantity);
    }

    [Fact]
    public void Transfer_NeverTakesStockTheSourceNeedsLaterItself()
    {
        // 02 holds 100 but has its own demand of 80 on day 20: it can only spare 20 to a shortage on day 10.
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0), St("02", 100) }, new[] { Dem("01", 100, 10), Dem("02", 80, 20) }), Opt());
        Assert.Equal(20m, run.PlannedOrders.Single(o => o.Type == PlannedOrderType.TransferRequest).Quantity);
        Assert.DoesNotContain(run.PlannedOrders, o => o.WarehouseCode == "02");          // 02 itself is never left short
    }

    [Fact]
    public void Transfer_CannotShipStockThatHasNotArrivedYet()
    {
        // 02 is empty today and a purchase order of 200 arrives on day 30: nothing to send for a day-10 shortage.
        var supply = new[] { new SupplyOrder { ItemCode = "X", WarehouseCode = "02", Type = SupplyType.PurchaseOrder, Quantity = 200, DueDate = Mon.AddDays(30), SourceDocKey = "OPOR:1" } };
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0), St("02", 0) }, new[] { Dem("01", 100, 10) }, supply), Opt());
        Assert.DoesNotContain(run.PlannedOrders, o => o.Type == PlannedOrderType.TransferRequest);
        Assert.Equal(100m, run.PlannedOrders.Single(o => o.Type == PlannedOrderType.PurchaseOrder).Quantity);
    }

    [Fact]
    public void Transfer_UsesTheLargestSpareFirst_AndSplitsAcrossSources()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0), St("02", 60), St("03", 70) }, new[] { Dem("01", 100, 10) }), Opt());
        var ts = run.PlannedOrders.Where(o => o.Type == PlannedOrderType.TransferRequest).OrderByDescending(o => o.Quantity).ToList();
        Assert.Equal(new[] { ("03", 70m), ("02", 30m) }, ts.Select(t => (t.FromWarehouse!, t.Quantity)).ToArray());
        Assert.DoesNotContain(run.PlannedOrders, o => o.Type == PlannedOrderType.PurchaseOrder);
    }

    [Fact]
    public void Transfer_OnlyFromTheChosenSourceWarehouses()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0), St("02", 500), St("03", 40) }, new[] { Dem("01", 100, 10) }), Opt(sources: "03"));
        var t = Assert.Single(run.PlannedOrders, o => o.Type == PlannedOrderType.TransferRequest);
        Assert.Equal(("03", 40m), (t.FromWarehouse, t.Quantity));          // 02 has plenty but is not an allowed source
        Assert.Equal(60m, run.PlannedOrders.Single(o => o.Type == PlannedOrderType.PurchaseOrder).Quantity);
    }

    [Fact]
    public void Transfer_Disabled_BuysAsBefore()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0), St("02", 300) }, new[] { Dem("01", 100, 10) }), Opt(transfers: false));
        Assert.DoesNotContain(run.PlannedOrders, o => o.Type == PlannedOrderType.TransferRequest);
        Assert.Equal(100m, run.PlannedOrders.Single().Quantity);
    }

    [Fact]
    public void Transfer_TwoShortagesShareOneSource_NothingIsGivenTwice()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0), St("03", 0), St("02", 100) },
            new[] { Dem("01", 80, 10), Dem("03", 80, 15) }), Opt());
        var ts = run.PlannedOrders.Where(o => o.Type == PlannedOrderType.TransferRequest).ToList();
        Assert.Equal(100m, ts.Sum(t => t.Quantity));                          // all 100 of 02 are handed out, once
        Assert.Equal(80m, ts.Single(t => t.WarehouseCode == "01").Quantity);  // the earlier shortage is served first
        Assert.Equal(20m, ts.Single(t => t.WarehouseCode == "03").Quantity);
        Assert.Equal(60m, run.PlannedOrders.Single(o => o.Type == PlannedOrderType.PurchaseOrder && o.WarehouseCode == "03").Quantity);
    }

    [Fact]
    public void Transfer_ReplacesPartOfAProductionOrder_ComponentsFollowWhatIsStillMade()
    {
        var items = new[]
        {
            new PlanItem { ItemCode = "BIKE", Procurement = ProcurementType.Make },
            Buy("WHEEL"),
        };
        var bom = new[] { new BomComponent { ParentItemCode = "BIKE", ComponentItemCode = "WHEEL", QuantityPer = 2 } };
        var stock = new[] { St("01", 0, "BIKE"), St("02", 6, "BIKE"), St("01", 0, "WHEEL") };
        var run = Run(Data(items, stock, new[] { Dem("01", 10, 20, "BIKE") }, bom: bom), Opt());

        Assert.Equal(6m, run.PlannedOrders.Single(o => o.Type == PlannedOrderType.TransferRequest && o.ItemCode == "BIKE").Quantity);
        var mo = run.PlannedOrders.Single(o => o.Type == PlannedOrderType.ProductionOrder);
        Assert.Equal(4m, mo.Quantity);
        // Wheels are needed for the 4 bikes still to be built (8), not for all 10 (20).
        Assert.Equal(8m, run.PlannedOrders.Single(o => o.ItemCode == "WHEEL").Quantity);
    }

    [Fact]
    public void Transfer_LotSizingAppliesToWhatIsLeftToBuy()
    {
        var run = Run(Data(new[] { Buy("X", i => i.MinOrderQty = 50) }, new[] { St("01", 0), St("02", 80) }, new[] { Dem("01", 100, 10) }), Opt());
        Assert.Equal(80m, run.PlannedOrders.Single(o => o.Type == PlannedOrderType.TransferRequest).Quantity);
        Assert.Equal(50m, run.PlannedOrders.Single(o => o.Type == PlannedOrderType.PurchaseOrder).Quantity);   // 20 left, minimum order 50
    }

    [Fact]
    public void Transfer_IsPeggedToTheDemandItCovers()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0), St("02", 300) }, new[] { Dem("01", 100, 10) }), Opt());
        var t = run.PlannedOrders.Single();
        var link = Assert.Single(run.Pegging, p => p.PlannedOrderId == t.Id);
        Assert.Equal(("SO-01-10", 100m), (link.DemandDocKey, link.Quantity));
    }

    [Fact]
    public void Transfer_ThatShouldAlreadyHaveGoneOut_RaisesReleaseInPast()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0), St("02", 300) }, new[] { Dem("01", 100, 0) }), Opt(lt: 3));
        var t = Assert.Single(run.PlannedOrders);
        var e = Assert.Single(run.Exceptions, x => x.Type == ExceptionType.ReleaseInPast);
        Assert.Equal(t.Id, e.PlannedOrderId);
        Assert.Contains("Transfer of", e.Message);
    }

    [Fact]
    public void Transfer_RequestNeverGoesOutOnAClosedDay()
    {
        // Needed Monday 19 Oct, 1 day transfer: Sunday is closed so the request goes out Friday.
        var cal = new WorkCalendar(new[] { DayOfWeek.Saturday, DayOfWeek.Sunday });
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0), St("02", 300) }, new[] { Dem("01", 100, 7) }, cal: cal), Opt());
        Assert.Equal(Mon.AddDays(4), run.PlannedOrders.Single().ReleaseDate);
    }

    [Fact]
    public void Transfer_ReceiptsAreNeverOfferedForExpediteOrDefer()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0), St("02", 300) }, new[] { Dem("01", 100, 10) }), Opt());
        Assert.DoesNotContain(run.Exceptions, e => e.Type is ExceptionType.Expedite or ExceptionType.Defer);
    }

    [Fact]
    public void Transfer_SingleWarehouse_NothingToTransfer()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0) }, new[] { Dem("01", 100, 10) }), Opt());
        Assert.Equal(PlannedOrderType.PurchaseOrder, run.PlannedOrders.Single().Type);
    }

    [Fact]
    public void Transfer_AppearsOnTheCalendar()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0), St("02", 300) }, new[] { Dem("01", 100, 10) }), Opt());
        Assert.Contains(run.Events, e => e.Kind == CalendarEventKind.PlannedRelease && e.Label.StartsWith("Request transfer from 02"));
        Assert.Contains(run.Events, e => e.Kind == CalendarEventKind.PlannedDue && e.Label == "Planned transfer receipt");
    }

    // ---- Company level ----------------------------------------------------------------------------------------

    [Fact]
    public void Company_PoolsStock_SoNothingIsBoughtWhenTheCompanyHasEnough()
    {
        var d = Data(new[] { Buy() }, new[] { St("01", 30), St("02", 70) }, new[] { Dem("01", 100, 10) });
        Assert.Empty(Run(d, Opt(transfers: false, company: true)).PlannedOrders);
        // Without pooling, warehouse 01 would buy what 02 already has.
        Assert.Equal(70m, Run(d, Opt(transfers: false)).PlannedOrders.Single(o => o.WarehouseCode == "01").Quantity);
    }

    [Fact]
    public void Company_BuysOnlyTheCompanyWideShortage()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 10), St("02", 30) }, new[] { Dem("01", 100, 10) }), Opt(transfers: false, company: true));
        Assert.Equal(60m, Assert.Single(run.PlannedOrders).Quantity);
    }

    [Fact]
    public void Company_NeverCreatesTransfers_EvenIfAskedTo()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0), St("02", 30) }, new[] { Dem("01", 100, 10) }), Opt(transfers: true, company: true));
        Assert.DoesNotContain(run.PlannedOrders, o => o.Type == PlannedOrderType.TransferRequest);
    }

    [Fact]
    public void Company_OrdersAreReceivedInTheItemsDefaultWarehouse()
    {
        var withHome = Run(Data(new[] { Buy("X", i => i.HomeWarehouse = "02") }, new[] { St("01", 10), St("02", 0) }, new[] { Dem("01", 100, 10) }),
            Opt(transfers: false, company: true));
        Assert.Equal("02", withHome.PlannedOrders.Single().WarehouseCode);
        Assert.All(withHome.Events.Where(e => e.Kind == CalendarEventKind.PlannedRelease), e => Assert.Equal("02", e.WarehouseCode));

        // No default warehouse: the warehouse with the most stock and demand.
        var noHome = Run(Data(new[] { Buy() }, new[] { St("01", 500), St("02", 5) }, new[] { Dem("01", 900, 10) }), Opt(transfers: false, company: true));
        Assert.Equal("01", noHome.PlannedOrders.Single().WarehouseCode);
    }

    [Fact]
    public void Company_PoolsOpenOrdersAcrossWarehouses()
    {
        var supply = new[] { new SupplyOrder { ItemCode = "X", WarehouseCode = "02", Type = SupplyType.PurchaseOrder, Quantity = 60, DueDate = Mon.AddDays(5), SourceDocKey = "OPOR:9" } };
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0), St("02", 0) }, new[] { Dem("01", 100, 10) }, supply), Opt(transfers: false, company: true));
        Assert.Equal(40m, run.PlannedOrders.Single().Quantity);
    }

    [Fact]
    public void Company_ExceptionsCarryARealWarehouseNotThePool()
    {
        var run = Run(Data(new[] { Buy("X", i => i.LeadTimeDays = 30) }, new[] { St("01", 10) }, new[] { Dem("01", 100, 5) }), Opt(transfers: false, company: true));
        var e = Assert.Single(run.Exceptions, x => x.Type == ExceptionType.ReleaseInPast);
        Assert.Equal("01", e.WarehouseCode);
        Assert.DoesNotContain(run.PlannedOrders, o => o.WarehouseCode == CompanyLevel.Pool);
    }

    [Fact]
    public void Company_ProductionStillExplodesComponents()
    {
        var items = new[] { new PlanItem { ItemCode = "BIKE", Procurement = ProcurementType.Make }, Buy("WHEEL") };
        var bom = new[] { new BomComponent { ParentItemCode = "BIKE", ComponentItemCode = "WHEEL", QuantityPer = 2 } };
        var stock = new[] { St("01", 0, "BIKE"), St("02", 0, "BIKE"), St("02", 6, "WHEEL") };
        var run = Run(Data(items, stock, new[] { Dem("01", 10, 20, "BIKE") }, bom: bom), Opt(transfers: false, company: true));
        Assert.Equal(10m, run.PlannedOrders.Single(o => o.ItemCode == "BIKE").Quantity);
        Assert.Equal(14m, run.PlannedOrders.Single(o => o.ItemCode == "WHEEL").Quantity);      // 20 needed - 6 held in the other warehouse
    }

    // ---- Purchase requests ------------------------------------------------------------------------------------

    [Fact]
    public void PurchaseRequest_ReplacesPurchaseOrders_ForPurchasedItemsOnly()
    {
        var items = new[] { new PlanItem { ItemCode = "BIKE", Procurement = ProcurementType.Make }, Buy("WHEEL", i => i.PreferredVendor = "V1") };
        var bom = new[] { new BomComponent { ParentItemCode = "BIKE", ComponentItemCode = "WHEEL", QuantityPer = 2 } };
        var run = Run(Data(items, new[] { St("01", 0, "BIKE"), St("01", 0, "WHEEL") }, new[] { Dem("01", 5, 20, "BIKE") }, bom: bom),
            Opt(transfers: false, doc: PurchaseDocument.Request));
        Assert.Equal(PlannedOrderType.PurchaseRequest, run.PlannedOrders.Single(o => o.ItemCode == "WHEEL").Type);
        Assert.Equal("V1", run.PlannedOrders.Single(o => o.ItemCode == "WHEEL").VendorCode);          // a request may still name the supplier
        Assert.Equal(PlannedOrderType.ProductionOrder, run.PlannedOrders.Single(o => o.ItemCode == "BIKE").Type);
    }

    [Fact]
    public void PurchaseRequest_DefaultIsPurchaseOrder()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0) }, new[] { Dem("01", 5, 5) }), Opt(transfers: false));
        Assert.Equal(PlannedOrderType.PurchaseOrder, run.PlannedOrders.Single().Type);
    }

    // ---- Criteria ---------------------------------------------------------------------------------------------

    [Fact]
    public void Criteria_CompanyLevelTurnsTransfersOff()
    {
        var c = new RunCriteria { Level = PlanningLevel.Company }; c.Transfers.Enabled = true;
        Assert.Null(c.Validate());
        Assert.False(c.Transfers.Enabled);
    }

    [Fact]
    public void Criteria_TransfersNeedTwoWarehouses_AndSourcesMustBeSelected()
    {
        var one = new RunCriteria(); one.Transfers.Enabled = true; one.Scope.Warehouses = new() { "01" };
        Assert.Contains("at least two", one.Validate());

        var outside = new RunCriteria(); outside.Transfers.Enabled = true; outside.Scope.Warehouses = new() { "01", "02" };
        outside.Transfers.SourceWarehouses = new() { "03" };
        Assert.Contains("not among the selected", outside.Validate());

        var fine = new RunCriteria(); fine.Transfers.Enabled = true; fine.Scope.Warehouses = new() { "01", "02" };
        fine.Transfers.SourceWarehouses = new() { " 02 ", "02" };
        Assert.Null(fine.Validate());
        Assert.Equal(new[] { "02" }, fine.Transfers.SourceWarehouses);

        var bad = new RunCriteria(); bad.Transfers.LeadTimeDays = 99;
        Assert.Contains("Transfer time", bad.Validate());
    }

    [Fact]
    public void Criteria_NewFieldsRoundTripAndMapToEngineOptions()
    {
        var c = new RunCriteria { Level = PlanningLevel.Warehouse };
        c.Purchasing.Document = PurchaseDocument.Request;
        c.Transfers.Enabled = true; c.Transfers.LeadTimeDays = 3; c.Transfers.SourceWarehouses = new() { "02" }; c.Scope.Warehouses = new() { "01", "02" };
        var back = RunCriteria.FromJson(c.ToJson())!;
        Assert.Equal(c.ToJson(), back.ToJson());
        Assert.Contains("\"purchasing\":{\"document\":\"Request\"}", c.ToJson());
        var o = CriteriaApplier.ToOptions(back, Mon, "u", null);
        Assert.True(o.TransferEnabled); Assert.False(o.CompanyLevel);
        Assert.Equal((PurchaseDocument.Request, 3), (o.PurchaseDocument, o.TransferLeadDays));
        Assert.Equal("02", Assert.Single(o.TransferSources));
    }
}
