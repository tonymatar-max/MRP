using CobaltMrp.Domain;
using CobaltMrp.Engine;
using Xunit;

namespace CobaltMrp.Engine.Tests;

/// <summary>QA pass 1: edge cases and conservation invariants for the engine (transfers, company pooling, lot sizing, BOM).</summary>
public class QaAdversarialTests
{
    private static readonly DateOnly Mon = new(2026, 10, 12);

    private static MrpOptions Opt(bool transfers = false, bool company = false, int days = 60, int bucket = 1, int lt = 1,
        string[]? whs = null, bool forecasts = true) => new()
    {
        HorizonStart = Mon, HorizonDays = days, BucketDays = bucket, TransferEnabled = transfers, TransferLeadDays = lt,
        CompanyLevel = company, Warehouses = whs ?? Array.Empty<string>(), UseForecasts = forecasts,
    };

    private static PlanItem Buy(string code = "X", Action<PlanItem>? cfg = null)
    { var i = new PlanItem { ItemCode = code, Procurement = ProcurementType.Buy }; cfg?.Invoke(i); return i; }
    private static PlanItem Make(string code, Action<PlanItem>? cfg = null)
    { var i = new PlanItem { ItemCode = code, Procurement = ProcurementType.Make }; cfg?.Invoke(i); return i; }
    private static WarehouseStock St(string wh, decimal oh, string item = "X") => new() { ItemCode = item, WarehouseCode = wh, OnHand = oh };
    private static DemandOrder Dem(string wh, decimal q, int day, string item = "X") => new()
        { ItemCode = item, WarehouseCode = wh, Source = DemandSource.SalesOrder, Quantity = q, DueDate = Mon.AddDays(day), SourceDocKey = $"SO-{wh}-{day}" };
    private static PlanningDataSet Data(PlanItem[] items, WarehouseStock[] stock, DemandOrder[] demand, SupplyOrder[]? supply = null,
        BomComponent[]? bom = null, Forecast[]? fc = null) =>
        new() { Items = items, Stock = stock, Demand = demand, Supply = supply ?? Array.Empty<SupplyOrder>(),
                Bom = bom ?? Array.Empty<BomComponent>(), Forecasts = fc ?? Array.Empty<Forecast>() };
    private static MrpRun Run(PlanningDataSet d, MrpOptions o) => new MrpEngine().Run(d, o);

    [Fact]
    public void EmptyDataSet_ProducesNothing_AndDoesNotThrow()
    {
        foreach (var co in new[] { false, true })
        foreach (var tr in new[] { false, true })
        {
            var run = Run(Data(Array.Empty<PlanItem>(), Array.Empty<WarehouseStock>(), Array.Empty<DemandOrder>()), Opt(tr, co));
            Assert.Empty(run.PlannedOrders);
        }
    }

    [Fact]
    public void WarehouseScopeThatMatchesNothing_ProducesNothing()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0) }, new[] { Dem("01", 10, 3) }), Opt(whs: new[] { "ZZ" }));
        Assert.Empty(run.PlannedOrders);
        var co = Run(Data(new[] { Buy() }, new[] { St("01", 0) }, new[] { Dem("01", 10, 3) }), Opt(company: true, whs: new[] { "ZZ" }));
        Assert.Empty(co.PlannedOrders);
    }

    [Fact]
    public void NegativeStock_IsNeverOfferedAsSpare()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0), St("02", -50) }, new[] { Dem("01", 10, 3) }), Opt(transfers: true));
        Assert.DoesNotContain(run.PlannedOrders, o => o.Type == PlannedOrderType.TransferRequest);
    }

    [Fact]
    public void ZeroQuantityDemand_CreatesNoOrder()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0) }, new[] { Dem("01", 0, 3) }), Opt());
        Assert.Empty(run.PlannedOrders);
    }

    [Fact]
    public void DemandBeyondTheHorizon_IsIgnored()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0) }, new[] { Dem("01", 10, 500) }), Opt());
        Assert.Empty(run.PlannedOrders);
    }

    [Fact]
    public void HugeHorizon_WeeklyBuckets_StaysCorrect()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0) }, new[] { Dem("01", 10, 3000) }), Opt(days: 3650, bucket: 7));
        var o = Assert.Single(run.PlannedOrders);
        Assert.Equal(10m, o.Quantity);
        Assert.True(o.DueDate <= Mon.AddDays(3000) && o.DueDate > Mon.AddDays(3000 - 7));
    }

    [Fact]
    public void CyclicBom_IsRejectedWithAClearError()
    {
        var items = new[] { Make("A"), Make("B") };
        var bom = new[] { new BomComponent { ParentItemCode = "A", ComponentItemCode = "B", QuantityPer = 1 },
                          new BomComponent { ParentItemCode = "B", ComponentItemCode = "A", QuantityPer = 1 } };
        var ex = Assert.Throws<InvalidOperationException>(() => Run(Data(items, new[] { St("01", 0, "A") }, new[] { Dem("01", 1, 1, "A") }, bom: bom), Opt()));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelfReferencingBom_IsRejected()
    {
        var bom = new[] { new BomComponent { ParentItemCode = "A", ComponentItemCode = "A", QuantityPer = 1 } };
        Assert.Throws<InvalidOperationException>(() => Run(Data(new[] { Make("A") }, new[] { St("01", 0, "A") }, new[] { Dem("01", 1, 1, "A") }, bom: bom), Opt()));
    }

    [Fact]
    public void DuplicateBomRows_AddUpTheComponentDemand()
    {
        var items = new[] { Make("A"), Buy("C") };
        var bom = new[] { new BomComponent { ParentItemCode = "A", ComponentItemCode = "C", QuantityPer = 2 },
                          new BomComponent { ParentItemCode = "A", ComponentItemCode = "C", QuantityPer = 3 } };
        var run = Run(Data(items, new[] { St("01", 0, "A"), St("01", 0, "C") }, new[] { Dem("01", 10, 10, "A") }, bom: bom), Opt());
        Assert.Equal(50m, run.PlannedOrders.Where(o => o.ItemCode == "C").Sum(o => o.Quantity));
    }

    [Fact]
    public void DuplicateStockRows_AreSummed()
    {
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 30), St("01", 30) }, new[] { Dem("01", 100, 3) }), Opt());
        Assert.Equal(40m, Assert.Single(run.PlannedOrders).Quantity);
    }

    [Fact]
    public void MultiLevelBom_AcrossWarehouses_ComponentDemandLandsInTheBomWarehouse()
    {
        var items = new[] { Make("TOP"), Make("SUB"), Buy("RAW") };
        var bom = new[]
        {
            new BomComponent { ParentItemCode = "TOP", ComponentItemCode = "SUB", QuantityPer = 2 },
            new BomComponent { ParentItemCode = "SUB", ComponentItemCode = "RAW", QuantityPer = 3, WarehouseCode = "02" },
        };
        var stock = new[] { St("01", 0, "TOP"), St("01", 0, "SUB"), St("02", 0, "RAW") };
        var run = Run(Data(items, stock, new[] { Dem("01", 5, 20, "TOP") }, bom: bom), Opt());
        Assert.Equal(10m, run.PlannedOrders.Single(o => o.ItemCode == "SUB").Quantity);
        var raw = run.PlannedOrders.Single(o => o.ItemCode == "RAW");
        Assert.Equal((30m, "02"), (raw.Quantity, raw.WarehouseCode));
    }

    [Fact]
    public void TransferWithLotSizing_MinOrderIsAppliedToThePurchaseOnly_NeverToTheTransfer()
    {
        var run = Run(Data(new[] { Buy("X", i => i.MinOrderQty = 100) }, new[] { St("01", 0), St("02", 30) }, new[] { Dem("01", 50, 10) }), Opt(true));
        var t = run.PlannedOrders.Single(o => o.Type == PlannedOrderType.TransferRequest);
        Assert.Equal(30m, t.Quantity);
        var p = run.PlannedOrders.Single(o => o.Type == PlannedOrderType.PurchaseOrder);
        Assert.Equal(100m, p.Quantity);       // 20 left, min order 100
    }

    [Fact]
    public void TransferSourceWithLaterDemand_AndSafetyStock_NeverGoesBelowFloorAtAnyDay()
    {
        var item = Buy("X", i => i.SafetyStock = 10);
        var d = Data(new[] { item }, new[] { St("01", 0), St("02", 100) }, new[] { Dem("01", 60, 5), Dem("02", 70, 30) });
        AssertFeasible(d, Run(d, Opt(true)));
    }

    [Fact]
    public void ItemInOnlyOneWarehouse_WithTransfersOn_BehavesAsWithout()
    {
        var d = Data(new[] { Buy() }, new[] { St("01", 5), St("02", 0, "Y") }, new[] { Dem("01", 100, 4) });
        var a = Run(d, Opt(true)); var b = Run(d, Opt(false));
        Assert.Equal(b.PlannedOrders.Select(o => (o.WarehouseCode, o.Quantity, o.DueDate)), a.PlannedOrders.Select(o => (o.WarehouseCode, o.Quantity, o.DueDate)));
    }

    [Fact]
    public void Company_ComponentWithNoStockRowAndNoHome_IsNotLeftInThePoolWarehouse()
    {
        // The component is reached only through the BOM; it has no stock row or demand of its own.
        var items = new[] { Make("A"), Buy("C") };
        var bom = new[] { new BomComponent { ParentItemCode = "A", ComponentItemCode = "C", QuantityPer = 2 } };
        var run = Run(Data(items, new[] { St("01", 0, "A"), St("02", 0, "A") }, new[] { Dem("01", 5, 10, "A") }, bom: bom), Opt(company: true));
        var c = run.PlannedOrders.Single(o => o.ItemCode == "C");
        Assert.NotEqual(CompanyLevel.Pool, c.WarehouseCode);
        Assert.Contains(c.WarehouseCode, new[] { "01", "02" });
    }

    [Fact]
    public void Company_ForecastsAcrossWarehouses_AreConsumedByPooledSalesOrders()
    {
        var fc = new[] { new Forecast { ItemCode = "X", WarehouseCode = "01", PeriodStart = Mon.AddDays(1), Quantity = 100 } };
        var run = Run(Data(new[] { Buy() }, new[] { St("01", 0), St("02", 0) }, new[] { Dem("02", 60, 5) }, fc: fc), Opt(company: true));
        Assert.Equal(100m, run.PlannedOrders.Sum(o => o.Quantity));   // 60 real + 40 unconsumed forecast, not 160
    }

    [Fact]
    public void MaxOrderQty_DoesNotLeaveTheBucketShort()
    {
        // 250 needed on day 5 but at most 100 per order: the shortage must be covered by orders due on day 5, not 6 and 7.
        var item = Buy("X", i => i.MaxOrderQty = 100);
        var run = Run(Data(new[] { item }, new[] { St("01", 0) }, new[] { Dem("01", 250, 5) }), Opt());
        Assert.Equal(250m, run.PlannedOrders.Sum(o => o.Quantity));
        Assert.All(run.PlannedOrders, o => Assert.Equal(Mon.AddDays(5), o.DueDate));
        Assert.All(run.PlannedOrders, o => Assert.True(o.Quantity <= 100m));
    }

    // ---- Conservation invariants over random data --------------------------------------------------------------

    private static int Ix(DateOnly x) => Math.Max(0, x.DayNumber - Mon.DayNumber);

    /// <summary>Replays the plan day by day per item and warehouse: stock never goes negative (nor below safety stock).</summary>
    private static void AssertFeasible(PlanningDataSet d, MrpRun run, string? tag = null)
    {
        foreach (var item in d.Items.Where(i => i.PlanningMethod != PlanningMethod.None))
        foreach (var wh in run.PlannedOrders.Select(o => o.WarehouseCode).Concat(d.Stock.Select(s => s.WarehouseCode)).Distinct())
        {
            var delta = new decimal[61];
            decimal poh = d.Stock.Where(s => s.ItemCode == item.ItemCode && s.WarehouseCode == wh).Sum(s => s.OnHand);
            foreach (var s in d.Supply.Where(s => s.ItemCode == item.ItemCode && s.WarehouseCode == wh)) if (Ix(s.DueDate) < 60) delta[Ix(s.DueDate)] += s.Quantity;
            foreach (var x in d.Demand.Where(s => s.ItemCode == item.ItemCode && s.WarehouseCode == wh)) if (Ix(x.DueDate) < 60) delta[Ix(x.DueDate)] -= x.Quantity;
            foreach (var o in run.PlannedOrders.Where(o => o.ItemCode == item.ItemCode))
            {
                if (o.WarehouseCode == wh && Ix(o.DueDate) < 60) delta[Ix(o.DueDate)] += o.Quantity;
                if (o.Type == PlannedOrderType.TransferRequest && o.FromWarehouse == wh) delta[Ix(o.ReleaseDate)] -= o.Quantity;
            }
            foreach (var o in run.PlannedOrders.Where(o => o.Type == PlannedOrderType.ProductionOrder))
                foreach (var c in d.Bom.Where(b => b.ParentItemCode == o.ItemCode && b.ComponentItemCode == item.ItemCode && (b.WarehouseCode ?? o.WarehouseCode) == wh))
                    delta[Ix(o.ReleaseDate)] -= o.Quantity * c.QuantityPer * (1 + c.ScrapFactor);
            var hasRow = d.Stock.Any(s => s.ItemCode == item.ItemCode && s.WarehouseCode == wh && s.OnHand != 0) || d.Demand.Any(s => s.ItemCode == item.ItemCode && s.WarehouseCode == wh) || d.Supply.Any(s => s.ItemCode == item.ItemCode && s.WarehouseCode == wh);
            for (var i = 0; i < 60; i++)
            {
                poh += delta[i];
                if (poh < -0.0001m)
                    Assert.Fail($"{tag} {item.ItemCode}@{wh} day {i}: projected {poh} is negative");
                if (hasRow && item.SafetyStock > 0 && poh < item.SafetyStock - 0.0001m && (d.Stock.Where(s => s.ItemCode == item.ItemCode && s.WarehouseCode == wh).Sum(s => s.OnHand) > 0 || i > 0))
                    Assert.Fail($"{tag} {item.ItemCode}@{wh} day {i}: projected {poh} below safety stock {item.SafetyStock} | " + string.Join("; ", d.Stock.Select(x => $"st {x.WarehouseCode}={x.OnHand}").Concat(d.Demand.Select(x => $"dem {x.WarehouseCode} {x.Quantity}@{Ix(x.DueDate)}")).Concat(d.Supply.Select(x => $"sup {x.WarehouseCode} {x.Quantity}@{Ix(x.DueDate)}")).Concat(run.PlannedOrders.Select(x => $"ord {x.WarehouseCode} {x.Quantity}@{Ix(x.DueDate)}"))));
            }
        }
    }

    [Fact]
    public void Fuzz_Transfers_PlanIsFeasible_And_TransfersNeverIncreasePurchases()
    {
        for (var seed = 1; seed <= 600; seed++)
        {
            var rnd = new Random(seed);
            var whs = new[] { "01", "02", "03" }.Take(2 + rnd.Next(2)).ToArray();
            var ss = rnd.Next(3) == 0 ? rnd.Next(1, 30) : 0;
            var item = Buy("X", i => { i.SafetyStock = ss; i.LeadTimeDays = rnd.Next(0, 5); });
            var stock = whs.Select(w => St(w, rnd.Next(0, 200))).ToArray();
            var demand = Enumerable.Range(0, rnd.Next(1, 8)).Select(_ => Dem(whs[rnd.Next(whs.Length)], rnd.Next(1, 120), rnd.Next(0, 55))).ToArray();
            var supply = rnd.Next(2) == 0 ? new[] { new SupplyOrder { ItemCode = "X", WarehouseCode = whs[rnd.Next(whs.Length)], Type = SupplyType.PurchaseOrder, Quantity = rnd.Next(1, 150), DueDate = Mon.AddDays(rnd.Next(0, 50)), SourceDocKey = "PO" } } : null;
            var d = Data(new[] { item }, stock, demand, supply);
            var withT = Run(d, Opt(true, lt: rnd.Next(0, 4)));
            var without = Run(d, Opt(false));
            AssertFeasible(d, without, $"seed {seed} plain");
            AssertFeasible(d, withT, $"seed {seed} transfers");
            var buy = withT.PlannedOrders.Where(o => o.Type != PlannedOrderType.TransferRequest).Sum(o => o.Quantity);
            var plain = without.PlannedOrders.Sum(o => o.Quantity);
            Assert.True(buy <= plain + 0.0001m, $"seed {seed}: transfers increased purchasing {buy} vs {plain}");
            foreach (var t in withT.PlannedOrders.Where(o => o.Type == PlannedOrderType.TransferRequest))
            {
                Assert.True(t.Quantity > 0, $"seed {seed}");
                Assert.NotEqual(t.FromWarehouse, t.WarehouseCode);
                Assert.True(t.ReleaseDate <= t.DueDate);
            }
        }
    }

    [Fact]
    public void Fuzz_Company_PlanIsFeasibleInThePool_NoPoolWarehouseLeaks()
    {
        for (var seed = 1; seed <= 400; seed++)
        {
            var rnd = new Random(seed);
            var whs = new[] { "01", "02", "03" }.Take(2 + rnd.Next(2)).ToArray();
            var items = new[] { Make("A"), Buy("C"), Buy("X") };
            var bom = new[] { new BomComponent { ParentItemCode = "A", ComponentItemCode = "C", QuantityPer = rnd.Next(1, 4), ScrapFactor = rnd.Next(2) * 0.1m } };
            var stock = whs.SelectMany(w => new[] { St(w, rnd.Next(0, 100), "X"), St(w, rnd.Next(0, 100), "A") }).ToArray();
            var demand = Enumerable.Range(0, rnd.Next(1, 8)).Select(_ => Dem(whs[rnd.Next(whs.Length)], rnd.Next(1, 120), rnd.Next(0, 55), rnd.Next(2) == 0 ? "X" : "A")).ToArray();
            var d = Data(items, stock, demand, bom: bom);
            var run = Run(d, Opt(company: true));
            Assert.DoesNotContain(run.PlannedOrders, o => o.WarehouseCode == CompanyLevel.Pool);
            Assert.DoesNotContain(run.Exceptions, e => e.WarehouseCode == CompanyLevel.Pool);
            foreach (var code in new[] { "X", "A", "C" })
            {
                var poh = stock.Where(s => s.ItemCode == code).Sum(s => s.OnHand);
                var delta = new decimal[61];
                foreach (var x in demand.Where(x => x.ItemCode == code)) delta[Ix(x.DueDate)] -= x.Quantity;
                foreach (var o in run.PlannedOrders.Where(o => o.ItemCode == code)) delta[Ix(o.DueDate)] += o.Quantity;
                if (code == "C") foreach (var o in run.PlannedOrders.Where(o => o.ItemCode == "A")) delta[Ix(o.ReleaseDate)] -= o.Quantity * bom[0].QuantityPer * (1 + bom[0].ScrapFactor);
                for (var i = 0; i < 60; i++) { poh += delta[i]; Assert.True(poh >= -0.0001m, $"seed {seed} {code} day {i} poh {poh}"); }
            }
        }
    }
}
