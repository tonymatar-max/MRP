using CobaltMrp.Domain;
using CobaltMrp.Engine;
using Xunit;

namespace CobaltMrp.Engine.Tests;

public class MrpEngineTests
{
    private static readonly DateOnly Today = new(2026, 10, 12);
    private static MrpOptions Opt(int days = 60) => new()
        { HorizonStart = Today, HorizonDays = days, BucketDays = 1, RunCapacityPlanning = true };

    private static PlanItem Item(string code, ProcurementType p, int lt = 0, Action<PlanItem>? cfg = null)
    {
        var i = new PlanItem { ItemCode = code, Procurement = p, LeadTimeDays = lt };
        cfg?.Invoke(i);
        return i;
    }

    private static WarehouseStock Stock(string item, decimal oh) =>
        new() { ItemCode = item, WarehouseCode = "01", OnHand = oh };

    private static DemandOrder So(string item, decimal qty, int dayOffset, string key = "SO1") =>
        new() { ItemCode = item, WarehouseCode = "01", Source = DemandSource.SalesOrder,
                Quantity = qty, DueDate = Today.AddDays(dayOffset), SourceDocKey = key };

    [Fact]
    public void LowLevelCode_ComponentUsedAtTwoDepths_GetsDeepest()
    {
        var items = new[]
        {
            Item("TOP", ProcurementType.Make), Item("SUB", ProcurementType.Make),
            Item("RAW", ProcurementType.Buy),
        };
        var bom = new[]
        {
            new BomComponent { ParentItemCode = "TOP", ComponentItemCode = "SUB", QuantityPer = 1 },
            new BomComponent { ParentItemCode = "TOP", ComponentItemCode = "RAW", QuantityPer = 1 },
            new BomComponent { ParentItemCode = "SUB", ComponentItemCode = "RAW", QuantityPer = 2 },
        };
        LowLevelCoder.Assign(items, bom);
        Assert.Equal(0, items[0].LowLevelCode);
        Assert.Equal(1, items[1].LowLevelCode);
        Assert.Equal(2, items[2].LowLevelCode); // RAW sits under SUB, so level 2
    }

    [Fact]
    public void LowLevelCode_Cycle_Throws()
    {
        var items = new[] { Item("A", ProcurementType.Make), Item("B", ProcurementType.Make) };
        var bom = new[]
        {
            new BomComponent { ParentItemCode = "A", ComponentItemCode = "B", QuantityPer = 1 },
            new BomComponent { ParentItemCode = "B", ComponentItemCode = "A", QuantityPer = 1 },
        };
        Assert.Throws<InvalidOperationException>(() => LowLevelCoder.Assign(items, bom));
    }

    [Fact]
    public void Netting_OnHandCoversDemand_NoPlannedOrder()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Item("X", ProcurementType.Buy, 5) },
            Stock = new[] { Stock("X", 100) },
            Demand = new[] { So("X", 40, 10) },
        };
        var run = new MrpEngine().Run(data, Opt());
        Assert.Empty(run.PlannedOrders);
    }

    [Fact]
    public void Netting_Shortage_PlansPurchaseWithLeadTimeOffset()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Item("X", ProcurementType.Buy, 7) },
            Stock = new[] { Stock("X", 30) },
            Demand = new[] { So("X", 100, 20) },
        };
        var run = new MrpEngine().Run(data, Opt());
        var po = Assert.Single(run.PlannedOrders);
        Assert.Equal(PlannedOrderType.PurchaseOrder, po.Type);
        Assert.Equal(70, po.Quantity);
        Assert.Equal(Today.AddDays(20), po.DueDate);
        Assert.Equal(Today.AddDays(13), po.ReleaseDate);
        var peg = Assert.Single(run.Pegging, p => p.PlannedOrderId == po.Id);
        Assert.Equal("SO1", peg.DemandDocKey);
    }

    [Fact]
    public void Netting_ScheduledReceiptReducesNeed()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Item("X", ProcurementType.Buy) },
            Stock = new[] { Stock("X", 0) },
            Demand = new[] { So("X", 100, 10) },
            Supply = new[]
            {
                new SupplyOrder { ItemCode = "X", WarehouseCode = "01", Type = SupplyType.PurchaseOrder,
                                  Quantity = 60, DueDate = Today.AddDays(5), SourceDocKey = "PO9" },
            },
        };
        var run = new MrpEngine().Run(data, Opt());
        Assert.Equal(40, Assert.Single(run.PlannedOrders).Quantity);
    }

    [Fact]
    public void BomExplosion_MakeItemCreatesDependentDemandOnComponent()
    {
        var data = new PlanningDataSet
        {
            Items = new[]
            {
                Item("BIKE", ProcurementType.Make, 3),
                Item("WHEEL", ProcurementType.Buy, 4),
            },
            Bom = new[]
            {
                new BomComponent { ParentItemCode = "BIKE", ComponentItemCode = "WHEEL", QuantityPer = 2 },
            },
            Stock = new[] { Stock("BIKE", 0), Stock("WHEEL", 0) },
            Demand = new[] { So("BIKE", 10, 20) },
        };
        var run = new MrpEngine().Run(data, Opt());

        var bike = Assert.Single(run.PlannedOrders, p => p.ItemCode == "BIKE");
        Assert.Equal(PlannedOrderType.ProductionOrder, bike.Type);
        Assert.Equal(Today.AddDays(17), bike.ReleaseDate);

        var wheel = Assert.Single(run.PlannedOrders, p => p.ItemCode == "WHEEL");
        Assert.Equal(20, wheel.Quantity);                       // 10 bikes x 2
        Assert.Equal(Today.AddDays(17), wheel.DueDate);         // needed when bike releases
        Assert.Equal(Today.AddDays(13), wheel.ReleaseDate);     // minus wheel lead time 4
        var peg = Assert.Single(run.Pegging, p => p.PlannedOrderId == wheel.Id);
        Assert.Equal(DemandSource.DependentDemand, peg.DemandSource);
        Assert.Equal("BIKE", peg.ParentItemCode);
    }

    [Fact]
    public void BomExplosion_ScrapFactorInflatesComponentDemand()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Item("P", ProcurementType.Make), Item("C", ProcurementType.Buy) },
            Bom = new[]
            {
                new BomComponent { ParentItemCode = "P", ComponentItemCode = "C",
                                   QuantityPer = 1, ScrapFactor = 0.10m },
            },
            Stock = new[] { Stock("P", 0), Stock("C", 0) },
            Demand = new[] { So("P", 100, 10) },
        };
        var run = new MrpEngine().Run(data, Opt());
        Assert.Equal(110, run.PlannedOrders.Single(p => p.ItemCode == "C").Quantity);
    }

    [Fact]
    public void LotSizing_MinOrderAndMultipleApplied()
    {
        var data = new PlanningDataSet
        {
            Items = new[]
            {
                Item("X", ProcurementType.Buy, 0, i => { i.MinOrderQty = 50; i.OrderMultiple = 25;
                                                         i.LotSizing = LotSizingRule.MinMaxMultiple; }),
            },
            Stock = new[] { Stock("X", 0) },
            Demand = new[] { So("X", 60, 5) },
        };
        var run = new MrpEngine().Run(data, Opt());
        Assert.Equal(75, Assert.Single(run.PlannedOrders).Quantity); // 60 -> >=50 -> multiple of 25
    }

    [Fact]
    public void SafetyStock_ForcesReplenishmentEvenWithoutDemand()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Item("X", ProcurementType.Buy, 0, i => i.SafetyStock = 20) },
            Stock = new[] { Stock("X", 5) },
        };
        var run = new MrpEngine().Run(data, Opt());
        Assert.Equal(15, Assert.Single(run.PlannedOrders).Quantity);
    }

    [Fact]
    public void ReorderPoint_TriggersWhenProjectedFallsToPoint()
    {
        var data = new PlanningDataSet
        {
            Items = new[]
            {
                Item("X", ProcurementType.Buy, 0, i =>
                {
                    i.PlanningMethod = PlanningMethod.ReorderPoint;
                    i.ReorderPoint = 10; i.LotSizing = LotSizingRule.FixedQuantity; i.FixedLotSize = 100;
                }),
            },
            Stock = new[] { Stock("X", 30) },
            Demand = new[] { So("X", 25, 5) },   // projected 5 < ROP 10
        };
        var run = new MrpEngine().Run(data, Opt());
        Assert.Equal(100, Assert.Single(run.PlannedOrders).Quantity);
    }

    [Fact]
    public void Exception_ReleaseInPast_WhenLeadTimeExceedsRunway()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Item("X", ProcurementType.Buy, 30) },
            Stock = new[] { Stock("X", 0) },
            Demand = new[] { So("X", 10, 5) },
        };
        var run = new MrpEngine().Run(data, Opt());
        Assert.Contains(run.Exceptions, e => e.Type == ExceptionType.ReleaseInPast);
    }

    [Fact]
    public void Exception_PastDueDemand_Flagged()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Item("X", ProcurementType.Buy) },
            Stock = new[] { Stock("X", 0) },
            Demand = new[] { So("X", 10, -3) },
        };
        var run = new MrpEngine().Run(data, Opt());
        Assert.Contains(run.Exceptions, e => e.Type == ExceptionType.PastDue);
    }

    [Fact]
    public void Forecast_NetOfConsumption_BecomesDemand()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Item("X", ProcurementType.Buy) },
            Stock = new[] { Stock("X", 0) },
            Forecasts = new[]
            {
                new Forecast { ItemCode = "X", WarehouseCode = "01",
                               PeriodStart = Today.AddDays(10), Quantity = 100, Consumed = 30 },
            },
        };
        Assert.Equal(70, Assert.Single(new MrpEngine().Run(data, Opt()).PlannedOrders).Quantity);
        var off = new MrpOptions { HorizonStart = Today, HorizonDays = 60, UseForecasts = false };
        Assert.Empty(new MrpEngine().Run(data, off).PlannedOrders);
    }

    [Fact]
    public void Crp_OverloadedWorkCenter_RaisesException()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Item("P", ProcurementType.Make, 2) },
            Stock = new[] { Stock("P", 0) },
            Demand = new[] { So("P", 100, 10) },
            WorkCenters = new[] { new WorkCenter { Code = "WC1", DailyCapacityHours = 8 } },
            Routings = new[]
            {
                new RoutingOperation { ItemCode = "P", Sequence = 10, WorkCenterCode = "WC1",
                                       HoursPerUnit = 0.5m, SetupHours = 1 },
            },
        };
        var run = new MrpEngine().Run(data, Opt());
        var load = Assert.Single(run.CapacityLoads);
        Assert.Equal(51m, load.RequiredHours);   // 1 + 100*0.5
        Assert.True(load.Overloaded);
        Assert.Contains(run.Exceptions, e => e.Type == ExceptionType.CapacityOverload);
    }

    [Fact]
    public void Run_IsDeterministic()
    {
        PlanningDataSet Build() => new()
        {
            Items = new[] { Item("A", ProcurementType.Make, 2), Item("B", ProcurementType.Buy, 3) },
            Bom = new[] { new BomComponent { ParentItemCode = "A", ComponentItemCode = "B", QuantityPer = 3 } },
            Stock = new[] { Stock("A", 1), Stock("B", 2) },
            Demand = new[] { So("A", 10, 9), So("A", 5, 15, "SO2") },
        };
        string Sig(MrpRun r) => string.Join("|", r.PlannedOrders
            .OrderBy(p => p.ItemCode).ThenBy(p => p.DueDate).Select(p => $"{p.ItemCode}:{p.Quantity}:{p.DueDate}"));
        Assert.Equal(Sig(new MrpEngine().Run(Build(), Opt())), Sig(new MrpEngine().Run(Build(), Opt())));
    }

    [Fact]
    public void Exception_ManyPastDueLines_AreAggregatedPerItem()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Item("X", ProcurementType.Buy) },
            Stock = new[] { Stock("X", 0) },
            Demand = new[] { So("X", 10, -3, "A"), So("X", 5, -9, "B"), So("X", 1, -1, "C") },
        };
        var run = new MrpEngine().Run(data, Opt());
        var pd = Assert.Single(run.Exceptions, e => e.Type == ExceptionType.PastDue);
        Assert.Equal(16, pd.Quantity);
        Assert.Equal(Today.AddDays(-9), pd.RelatedDate);
        Assert.Equal(16, Assert.Single(run.PlannedOrders).Quantity);   // still planned, folded to bucket 0
    }

    [Fact]
    public void Exception_DemandForItemNotInPlan_IsReportedNotDropped()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Item("X", ProcurementType.Buy) },
            Stock = new[] { Stock("X", 100) },
            Demand = new[] { So("GHOST", 7, 5), So("GHOST", 3, 6, "SO2") },
        };
        var run = new MrpEngine().Run(data, Opt());
        var e = Assert.Single(run.Exceptions, x => x.Type == ExceptionType.NotPlanned);
        Assert.Equal("GHOST", e.ItemCode);
        Assert.Equal(10, e.Quantity);
    }

    [Fact]
    public void Forecast_PeriodBeforePlanStart_IsIgnored()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Item("X", ProcurementType.Buy) },
            Stock = new[] { Stock("X", 0) },
            Forecasts = new[]
            {
                new Forecast { ItemCode = "X", WarehouseCode = "01", PeriodStart = Today.AddDays(-400), Quantity = 500 },
                new Forecast { ItemCode = "X", WarehouseCode = "01", PeriodStart = Today.AddDays(10), Quantity = 20 },
            },
        };
        var run = new MrpEngine().Run(data, Opt());
        Assert.Equal(20, Assert.Single(run.PlannedOrders).Quantity);
        Assert.DoesNotContain(run.Exceptions, e => e.Type == ExceptionType.PastDue);

        var kept = new MrpOptions { HorizonStart = Today, HorizonDays = 60, ForecastLookbackDays = 500 };
        Assert.Equal(520, new MrpEngine().Run(data, kept).PlannedOrders.Sum(o => o.Quantity));
    }

    private static WarehouseStock StockAt(string item, string wh, decimal oh) =>
        new() { ItemCode = item, WarehouseCode = wh, OnHand = oh };

    [Fact]
    public void ItemPolicy_IsNotAppliedToEmptyWarehousesThatOnlyHaveAStockRow()
    {
        // B1 creates a stock row in every warehouse; the min/max policy must not fire in the empty ones.
        var data = new PlanningDataSet
        {
            Items = new[]
            {
                Item("X", ProcurementType.Buy, 0, i =>
                { i.PlanningMethod = PlanningMethod.MinMax; i.ReorderPoint = 120; i.SafetyStock = 120; i.MaxStockLevel = 300; }),
            },
            Stock = new[] { StockAt("X", "01", 659), StockAt("X", "02", 0), StockAt("X", "03", 0) },
        };
        Assert.Empty(new MrpEngine().Run(data, Opt()).PlannedOrders);   // 659 >= min, others not planned
    }

    [Fact]
    public void ItemPolicy_ZeroStockEverywhere_UsesHomeWarehouse()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Item("X", ProcurementType.Buy, 0, i => { i.SafetyStock = 10; i.HomeWarehouse = "02"; }) },
            Stock = new[] { StockAt("X", "01", 0), StockAt("X", "02", 0), StockAt("X", "03", 0) },
        };
        var po = Assert.Single(new MrpEngine().Run(data, Opt()).PlannedOrders);
        Assert.Equal("02", po.WarehouseCode);
        Assert.Equal(10, po.Quantity);
    }

    [Fact]
    public void ItemPolicy_ZeroStockEverywhere_NoHome_IsReportedNotGuessed()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Item("X", ProcurementType.Buy, 0, i => i.SafetyStock = 10) },
            Stock = new[] { StockAt("X", "01", 0), StockAt("X", "02", 0) },
        };
        var run = new MrpEngine().Run(data, Opt());
        Assert.Empty(run.PlannedOrders);
        Assert.Contains(run.Exceptions, e => e.Type == ExceptionType.NotPlanned && e.ItemCode == "X");
    }

    [Fact]
    public void ItemPolicy_SingleStockLocation_IsUnambiguous()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Item("X", ProcurementType.Buy, 0, i => i.SafetyStock = 10) },
            Stock = new[] { StockAt("X", "01", 0) },
        };
        Assert.Equal(10, Assert.Single(new MrpEngine().Run(data, Opt()).PlannedOrders).Quantity);
    }
}
