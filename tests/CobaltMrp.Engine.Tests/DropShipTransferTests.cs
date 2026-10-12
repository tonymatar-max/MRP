using CobaltMrp.Domain;
using CobaltMrp.Engine;
using Xunit;

namespace CobaltMrp.Engine.Tests;

/// <summary>SAP B1 refuses transfers to or from a Drop Ship warehouse ("available only in sales order"): the plan must not create them.</summary>
public class DropShipTransferTests
{
    private static readonly DateOnly Mon = new(2026, 10, 12);

    private static MrpOptions Opt(params string[] dropShip) => new()
    {
        HorizonStart = Mon, HorizonDays = 60, BucketDays = 1, TransferEnabled = true, TransferLeadDays = 1, NoTransferWarehouses = dropShip,
    };

    private static PlanningDataSet Data(WarehouseStock[] stock, DemandOrder[] demand) => new()
    {
        Items = new[] { new PlanItem { ItemCode = "X", Procurement = ProcurementType.Buy } }, Stock = stock, Demand = demand,
    };

    private static WarehouseStock St(string wh, decimal oh) => new() { ItemCode = "X", WarehouseCode = wh, OnHand = oh };
    private static DemandOrder Dem(string wh, decimal q, int day) => new()
        { ItemCode = "X", WarehouseCode = wh, Source = DemandSource.SalesOrder, Quantity = q, DueDate = Mon.AddDays(day), SourceDocKey = $"SO-{wh}-{day}" };

    [Fact]
    public void WithoutDropShip_TheSpareStockIsTransferred_Baseline()
    {
        var run = new MrpEngine().Run(Data(new[] { St("01", 0), St("03", 300) }, new[] { Dem("01", 100, 10) }), Opt());
        Assert.Contains(run.PlannedOrders, o => o.Type == PlannedOrderType.TransferRequest && o.FromWarehouse == "03");
    }

    [Fact]
    public void DropShipWarehouse_IsNeverATransferSource()
    {
        var run = new MrpEngine().Run(Data(new[] { St("01", 0), St("03", 300) }, new[] { Dem("01", 100, 10) }), Opt("03"));
        Assert.DoesNotContain(run.PlannedOrders, o => o.Type == PlannedOrderType.TransferRequest);
        var po = Assert.Single(run.PlannedOrders);
        Assert.Equal((PlannedOrderType.PurchaseOrder, "01", 100m), (po.Type, po.WarehouseCode, po.Quantity));   // bought instead
    }

    [Fact]
    public void DropShipWarehouse_IsNeverATransferTarget()
    {
        // 03 is short and 01 has plenty, but 03 is Drop Ship: its shortage is bought, not transferred.
        var run = new MrpEngine().Run(Data(new[] { St("01", 500), St("03", 0) }, new[] { Dem("03", 100, 10) }), Opt("03"));
        Assert.DoesNotContain(run.PlannedOrders, o => o.Type == PlannedOrderType.TransferRequest);
        Assert.Contains(run.PlannedOrders, o => o.Type == PlannedOrderType.PurchaseOrder && o.WarehouseCode == "03");
    }

    [Fact]
    public void DropShipNameComparison_IgnoresCase()
    {
        var run = new MrpEngine().Run(Data(new[] { St("01", 0), St("dS", 300) }, new[] { Dem("01", 100, 10) }), Opt("DS"));
        Assert.DoesNotContain(run.PlannedOrders, o => o.Type == PlannedOrderType.TransferRequest);
    }
}
