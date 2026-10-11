using CobaltMrp.Domain;
using CobaltMrp.Engine;
using Xunit;

namespace CobaltMrp.Engine.Tests;

/// <summary>Working calendar, forecast consumption, vendors and calendar events.</summary>
public class PlanningRealismTests
{
    // 2026-10-12 is a Monday.
    private static readonly DateOnly Mon = new(2026, 10, 12);
    private static MrpOptions Opt(int days = 60, bool consume = true) => new()
        { HorizonStart = Mon, HorizonDays = days, BucketDays = 1, ConsumeForecast = consume };

    private static PlanItem Buy(string code, int lt = 0, Action<PlanItem>? cfg = null)
    { var i = new PlanItem { ItemCode = code, Procurement = ProcurementType.Buy, LeadTimeDays = lt }; cfg?.Invoke(i); return i; }

    private static WarehouseStock Stock(string item, decimal oh = 0) => new() { ItemCode = item, WarehouseCode = "01", OnHand = oh };
    private static DemandOrder So(string item, decimal q, DateOnly due, string key = "SO") =>
        new() { ItemCode = item, WarehouseCode = "01", Source = DemandSource.SalesOrder, Quantity = q, DueDate = due, SourceDocKey = key };
    private static Forecast Fc(string item, DateOnly start, decimal q, decimal consumed = 0) =>
        new() { ItemCode = item, WarehouseCode = "01", PeriodStart = start, Quantity = q, Consumed = consumed };

    private static WorkCalendar MonFri() => new(new[] { DayOfWeek.Saturday, DayOfWeek.Sunday });

    // ---- WorkCalendar -----------------------------------------------------------------------------------

    [Fact]
    public void WorkCalendar_WeekendsAndClosures()
    {
        var cal = new WorkCalendar(new[] { DayOfWeek.Saturday, DayOfWeek.Sunday },
            new[] { new KeyValuePair<DateOnly, string>(Mon.AddDays(2), "Founders Day") });
        Assert.True(cal.IsWorkingDay(Mon));
        Assert.Equal("Weekend", cal.NonWorkingReason(Mon.AddDays(5)));        // Saturday
        Assert.Equal("Founders Day", cal.NonWorkingReason(Mon.AddDays(2)));   // Wednesday closure
        Assert.Equal(4, cal.WorkingDays(Mon, 7));                              // Mon, Tue, Thu, Fri
        Assert.Equal(Mon.AddDays(4), cal.OnOrBeforeWorkingDay(Mon.AddDays(6)));  // Sunday -> Friday
    }

    [Fact]
    public void WorkCalendar_AllDays_EveryDayWorks()
    {
        Assert.True(WorkCalendar.AllDays.IsWorkingDay(Mon.AddDays(5)));
        Assert.Equal(7, WorkCalendar.AllDays.WorkingDays(Mon, 7));
    }

    // ---- Release dates and capacity use the calendar ----------------------------------------------------

    [Fact]
    public void Release_NeverLandsOnANonWorkingDay()
    {
        // Needed Monday 19 Oct with a 1-day lead time: Sunday 18 Oct is closed, so release moves to Friday 16 Oct.
        var data = new PlanningDataSet
        {
            Items = new[] { Buy("X", 1) }, Stock = new[] { Stock("X") },
            Demand = new[] { So("X", 5, Mon.AddDays(7)) }, Calendar = MonFri(),
        };
        var po = Assert.Single(new MrpEngine().Run(data, Opt()).PlannedOrders);
        Assert.Equal(Mon.AddDays(7), po.DueDate);
        Assert.Equal(Mon.AddDays(4), po.ReleaseDate);
    }

    [Fact]
    public void Capacity_UsesWorkingDaysInTheBucket()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { new PlanItem { ItemCode = "P", Procurement = ProcurementType.Make } },
            Stock = new[] { Stock("P") }, Demand = new[] { So("P", 100, Mon.AddDays(10)) },
            WorkCenters = new[] { new WorkCenter { Code = "WC", DailyCapacityHours = 8 } },
            Routings = new[] { new RoutingOperation { ItemCode = "P", Sequence = 10, WorkCenterCode = "WC", HoursPerUnit = 0.1m } },
            Calendar = MonFri(),
        };
        var opt = new MrpOptions { HorizonStart = Mon, HorizonDays = 28, BucketDays = 7 };
        var load = Assert.Single(new MrpEngine().Run(data, opt).CapacityLoads);
        Assert.Equal(40m, load.AvailableHours);     // 5 working days x 8h, not 7 x 8h
        Assert.Equal(10m, load.RequiredHours);
    }

    // ---- Forecast consumption ---------------------------------------------------------------------------

    private static PlanningDataSet ForecastData(params DemandOrder[] sales) => new()
    {
        Items = new[] { Buy("X") }, Stock = new[] { Stock("X") }, Demand = sales,
        Forecasts = new[] { Fc("X", Mon.AddDays(7), 100), Fc("X", Mon.AddDays(37), 100) },
    };

    [Fact]
    public void ForecastConsumption_SalesOrdersInThePeriodReplaceForecast()
    {
        // 60 of real sales inside the first forecast period consume 60 of its 100.
        var run = new MrpEngine().Run(ForecastData(So("X", 60, Mon.AddDays(10))), Opt());
        var total = run.PlannedOrders.Sum(o => o.Quantity);
        Assert.Equal(60 + 40 + 100, total);   // sales 60 + forecast remainder 40 + second period 100
    }

    [Fact]
    public void ForecastConsumption_SalesBeyondTheForecast_NeverGoNegative()
    {
        var run = new MrpEngine().Run(ForecastData(So("X", 150, Mon.AddDays(10))), Opt());
        Assert.Equal(150 + 0 + 100, run.PlannedOrders.Sum(o => o.Quantity));
    }

    [Fact]
    public void ForecastConsumption_Off_CountsBoth()
    {
        var run = new MrpEngine().Run(ForecastData(So("X", 60, Mon.AddDays(10))), Opt(consume: false));
        Assert.Equal(60 + 100 + 100, run.PlannedOrders.Sum(o => o.Quantity));
    }

    [Fact]
    public void ForecastConsumption_SalesOutsideThePeriodDoNotConsume()
    {
        // Sales on day 20 fall in the SECOND period only if after day 37; here they sit between: still period 1 (7..37).
        var run = new MrpEngine().Run(ForecastData(So("X", 30, Mon.AddDays(40))), Opt());
        Assert.Equal(30 + 100 + 70, run.PlannedOrders.Sum(o => o.Quantity));   // 40 lands in period 2, consuming 30 of it
    }

    // ---- Vendors ----------------------------------------------------------------------------------------

    [Fact]
    public void PreferredVendor_FlowsToPurchaseOrdersOnly()
    {
        var data = new PlanningDataSet
        {
            Items = new[]
            {
                Buy("BUYME", 0, i => i.PreferredVendor = "V10000"),
                new PlanItem { ItemCode = "MAKEME", Procurement = ProcurementType.Make, PreferredVendor = "V99" },
            },
            Stock = new[] { Stock("BUYME"), Stock("MAKEME") },
            Demand = new[] { So("BUYME", 5, Mon.AddDays(3)), So("MAKEME", 5, Mon.AddDays(3)) },
        };
        var run = new MrpEngine().Run(data, Opt());
        Assert.Equal("V10000", run.PlannedOrders.Single(o => o.ItemCode == "BUYME").VendorCode);
        Assert.Null(run.PlannedOrders.Single(o => o.ItemCode == "MAKEME").VendorCode);
    }

    // ---- Calendar events --------------------------------------------------------------------------------

    [Fact]
    public void Events_CoverDemandSupplyPlannedAndExceptions()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Buy("X", 5) }, Stock = new[] { Stock("X") },
            Demand = new[] { So("X", 10, Mon.AddDays(20), "ORDR:1") },
            Supply = new[] { new SupplyOrder { ItemCode = "X", WarehouseCode = "01", Type = SupplyType.PurchaseOrder,
                                               Quantity = 3, DueDate = Mon.AddDays(8), SourceDocKey = "OPOR:9" } },
            Forecasts = new[] { Fc("X", Mon.AddDays(30), 50) },
        };
        var run = new MrpEngine().Run(data, Opt());
        var kinds = run.Events.Select(e => e.Kind).ToHashSet();
        Assert.Contains(CalendarEventKind.Demand, kinds);
        Assert.Contains(CalendarEventKind.Supply, kinds);
        Assert.Contains(CalendarEventKind.Forecast, kinds);
        Assert.Contains(CalendarEventKind.PlannedRelease, kinds);
        Assert.Contains(CalendarEventKind.PlannedDue, kinds);
        var order = run.PlannedOrders.First();
        Assert.Contains(run.Events, e => e.Kind == CalendarEventKind.PlannedRelease && e.Ref == order.Id.ToString());
        Assert.Contains(run.Events, e => e.Kind == CalendarEventKind.Demand && e.Ref == "ORDR:1" && e.Date == Mon.AddDays(20));
    }

    [Fact]
    public void Events_OverdueRelease_IsPlacedOnDayOneAsDanger()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Buy("X", 30) }, Stock = new[] { Stock("X") },
            Demand = new[] { So("X", 5, Mon.AddDays(5)) },
        };
        var run = new MrpEngine().Run(data, Opt());
        var ev = Assert.Single(run.Events, e => e.Kind == CalendarEventKind.PlannedRelease);
        Assert.Equal(Mon, ev.Date);
        Assert.Equal("danger", ev.Severity);
    }

    [Fact]
    public void Events_OverloadedWorkCenter_AppearsAsCapacityEvent()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { new PlanItem { ItemCode = "P", Procurement = ProcurementType.Make } },
            Stock = new[] { Stock("P") }, Demand = new[] { So("P", 100, Mon.AddDays(5)) },
            WorkCenters = new[] { new WorkCenter { Code = "WC", DailyCapacityHours = 8 } },
            Routings = new[] { new RoutingOperation { ItemCode = "P", Sequence = 10, WorkCenterCode = "WC", HoursPerUnit = 1 } },
        };
        var run = new MrpEngine().Run(data, Opt());
        Assert.Contains(run.Events, e => e.Kind == CalendarEventKind.CapacityOverload && e.Ref == "WC");
    }

    [Fact]
    public void Events_PastDueDemand_FoldsOntoDayOneNotLost()
    {
        var data = new PlanningDataSet
        {
            Items = new[] { Buy("X") }, Stock = new[] { Stock("X") },
            Demand = new[] { So("X", 5, Mon.AddDays(-30)) },
        };
        var run = new MrpEngine().Run(data, Opt());
        Assert.Contains(run.Events, e => e.Kind == CalendarEventKind.Exception && e.Date == Mon && e.Ref == "PastDue");
    }
}
