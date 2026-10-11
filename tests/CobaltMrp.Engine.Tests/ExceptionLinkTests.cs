using CobaltMrp.Domain;
using CobaltMrp.Engine;
using Xunit;

namespace CobaltMrp.Engine.Tests;

/// <summary>Exceptions carry what is needed to act on them: ids, the planned order, the existing document and dates.</summary>
public class ExceptionLinkTests
{
    private static readonly DateOnly Mon = new(2026, 10, 12);
    private static MrpOptions Opt() => new() { HorizonStart = Mon, HorizonDays = 60, BucketDays = 1 };

    private static PlanItem Buy(string code, int lt = 0) => new() { ItemCode = code, Procurement = ProcurementType.Buy, LeadTimeDays = lt };
    private static WarehouseStock Stock(string i, decimal oh = 0) => new() { ItemCode = i, WarehouseCode = "01", OnHand = oh };
    private static DemandOrder So(string i, decimal q, int day) => new()
        { ItemCode = i, WarehouseCode = "01", Source = DemandSource.SalesOrder, Quantity = q, DueDate = Mon.AddDays(day), SourceDocKey = "SO" };

    [Fact]
    public void ReleaseInPast_PointsAtTheOrderThatMustGoOutAlready()
    {
        var data = new PlanningDataSet { Items = new[] { Buy("X", 30) }, Stock = new[] { Stock("X") }, Demand = new[] { So("X", 5, 5) } };
        var run = new MrpEngine().Run(data, Opt());
        var e = Assert.Single(run.Exceptions, x => x.Type == ExceptionType.ReleaseInPast);
        var po = Assert.Single(run.PlannedOrders);
        Assert.Equal(po.Id, e.PlannedOrderId);
        Assert.NotEqual(Guid.Empty, e.ExceptionId);
        Assert.Null(e.ActionStatus);
    }

    [Fact]
    public void Expedite_NamesTheExistingOrderLineAndBothDates()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Buy("X") }, Stock = new[] { Stock("X") }, Demand = new[] { So("X", 100, 10) },
            Supply = new[] { new SupplyOrder { ItemCode = "X", WarehouseCode = "01", Type = SupplyType.PurchaseOrder, Quantity = 60,
                                               DueDate = Mon.AddDays(20), SourceDocKey = "OPOR:4501", SourceLine = 2 } },
        };
        var run = new MrpEngine().Run(data, Opt());
        var e = Assert.Single(run.Exceptions, x => x.Type == ExceptionType.Expedite);
        Assert.Equal(("OPOR:4501", 2), (e.SourceDocKey, e.SourceLine));
        Assert.Equal((Mon.AddDays(20), Mon.AddDays(10)), (e.CurrentDate, e.ProposedDate));
        Assert.Contains(run.PlannedOrders, p => p.Id == e.PlannedOrderId);
    }

    [Fact]
    public void Expedite_NeverProposesADateBeforeThePlanStarts()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Buy("X") }, Stock = new[] { Stock("X") }, Demand = new[] { So("X", 100, -4) },    // overdue demand folds onto day one
            Supply = new[] { new SupplyOrder { ItemCode = "X", WarehouseCode = "01", Type = SupplyType.PurchaseOrder, Quantity = 60,
                                               DueDate = Mon.AddDays(20), SourceDocKey = "OPOR:1" } },
        };
        var e = Assert.Single(new MrpEngine().Run(data, Opt()).Exceptions, x => x.Type == ExceptionType.Expedite);
        Assert.True(e.ProposedDate >= Mon);
    }

    [Fact]
    public void Defer_ProposesThePlanEndOnlyWhenTheOrderIsDueInsideThePlan()
    {
        PlanningDataSet Data(int dueDay) => new()
        {
            Items = new[] { Buy("X") }, Stock = new[] { Stock("X", 500) },
            Supply = new[] { new SupplyOrder { ItemCode = "X", WarehouseCode = "01", Type = SupplyType.PurchaseOrder, Quantity = 50,
                                               DueDate = Mon.AddDays(dueDay), SourceDocKey = "OPOR:7", SourceLine = 0 } },
        };
        var inside = Assert.Single(new MrpEngine().Run(Data(20), Opt()).Exceptions, x => x.Type == ExceptionType.Defer);
        Assert.Equal(("OPOR:7", 0, Mon.AddDays(20)), (inside.SourceDocKey, inside.SourceLine, inside.CurrentDate));
        Assert.Equal(Mon.AddDays(60), inside.ProposedDate);

        var run = new MrpEngine().Run(Data(70), Opt());      // already beyond the plan: nothing sensible to propose
        var beyond = run.Exceptions.FirstOrDefault(x => x.Type == ExceptionType.Defer);
        if (beyond is not null) Assert.Null(beyond.ProposedDate);
    }

    [Fact]
    public void ExceptionIds_AreUnique()
    {
        var data = new PlanningDataSet { Items = new[] { Buy("X", 30), Buy("Y", 30) }, Stock = new[] { Stock("X"), Stock("Y") },
                                         Demand = new[] { So("X", 5, 5), So("Y", 5, 5) } };
        var ids = new MrpEngine().Run(data, Opt()).Exceptions.Select(e => e.ExceptionId).ToList();
        Assert.True(ids.Count >= 2);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void OneOrderLine_NeverGetsBothExpediteAndDefer()
    {
        // Short on day 10, the open order only arrives on day 20: MRP plans a new order for the gap, which makes the
        // late order look like surplus. The planner should see only the Expedite for it.
        var data = new PlanningDataSet
        {
            Items = new[] { Buy("X") }, Stock = new[] { Stock("X") }, Demand = new[] { So("X", 20, 10) },
            Supply = new[] { new SupplyOrder { ItemCode = "X", WarehouseCode = "01", Type = SupplyType.PurchaseOrder, Quantity = 50,
                                               DueDate = Mon.AddDays(20), SourceDocKey = "OPOR:4601", SourceLine = 0 } },
        };
        var run = new MrpEngine().Run(data, Opt());
        Assert.Single(run.Exceptions, e => e.Type == ExceptionType.Expedite && e.SourceDocKey == "OPOR:4601");
        Assert.DoesNotContain(run.Exceptions, e => e.Type == ExceptionType.Defer && e.SourceDocKey == "OPOR:4601");
    }

    [Fact]
    public void PureSurplus_StillGetsItsDefer()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Buy("X") }, Stock = new[] { Stock("X", 500) },
            Supply = new[] { new SupplyOrder { ItemCode = "X", WarehouseCode = "01", Type = SupplyType.PurchaseOrder, Quantity = 50,
                                               DueDate = Mon.AddDays(20), SourceDocKey = "OPOR:4602", SourceLine = 0 } },
        };
        Assert.Single(new MrpEngine().Run(data, Opt()).Exceptions, e => e.Type == ExceptionType.Defer && e.SourceDocKey == "OPOR:4602");
    }
}
