using CobaltMrp.Domain;

namespace CobaltMrp.Engine;

/// <summary>
/// Collects everything that has a date inside the planning horizon into one list for the Calendar screen:
/// demand, forecasts, open supply, recommended releases and due dates, planner action messages and capacity
/// overloads. Past-due items fold onto the first day of the horizon (they are already overdue).
/// </summary>
public static class CalendarEventBuilder
{
    public static void Build(MrpRun run, PlanningDataSet data, IReadOnlyList<NetForecast> forecasts,
                             Func<string, bool> whAllowed, BucketCalendar cal, WorkCalendar workCalendar)
    {
        bool InHorizon(DateOnly d) => d >= cal.Start && d < cal.End;
        void Add(DateOnly date, CalendarEventKind kind, string item, string? wh, decimal qty,
                 string label, string? reference, string severity = "info") =>
            run.Events.Add(new CalendarEvent
            {
                Date = date, Kind = kind, ItemCode = item, WarehouseCode = wh, Quantity = qty,
                Label = label, Ref = reference, Severity = severity,
            });

        for (var day = cal.Start; day < cal.End; day = day.AddDays(1))
            if (workCalendar.NonWorkingReason(day) is { } why)
                Add(day, CalendarEventKind.NonWorking, "", null, 0, why, null);

        foreach (var d in data.Demand.Where(d => whAllowed(d.WarehouseCode) && InHorizon(d.DueDate)))
            Add(d.DueDate, CalendarEventKind.Demand, d.ItemCode, d.WarehouseCode, d.Quantity,
                "Sales order due", d.SourceDocKey);

        foreach (var f in forecasts.Where(f => InHorizon(f.Source.PeriodStart)))
            Add(f.Source.PeriodStart, CalendarEventKind.Forecast, f.Source.ItemCode, f.Source.WarehouseCode,
                f.Net, "Forecast demand (net of sales orders)", "FCST");

        foreach (var s in data.Supply.Where(s => whAllowed(s.WarehouseCode) && InHorizon(s.DueDate)))
            Add(s.DueDate, CalendarEventKind.Supply, s.ItemCode, s.WarehouseCode, s.Quantity,
                s.Type == SupplyType.ProductionOrder ? "Production order due" : "Purchase order due", s.SourceDocKey);

        foreach (var o in run.PlannedOrders)
        {
            var overdue = o.ReleaseDate < cal.Start;
            var (releaseLabel, dueLabel) = o.Type switch
            {
                PlannedOrderType.ProductionOrder => ("Release production order", "Planned production receipt"),
                PlannedOrderType.TransferRequest => ($"Request transfer from {o.FromWarehouse}", "Planned transfer receipt"),
                PlannedOrderType.PurchaseRequest => ("Raise purchase request", "Planned purchase receipt"),
                _ => ("Place purchase order", "Planned purchase receipt"),
            };
            Add(overdue ? cal.Start : o.ReleaseDate, CalendarEventKind.PlannedRelease, o.ItemCode, o.WarehouseCode,
                o.Quantity, releaseLabel + (overdue ? $" (was due {o.ReleaseDate:yyyy-MM-dd})" : ""),
                o.Id.ToString(), overdue ? "danger" : "info");
            if (InHorizon(o.DueDate))
                Add(o.DueDate, CalendarEventKind.PlannedDue, o.ItemCode, o.WarehouseCode, o.Quantity, dueLabel, o.Id.ToString());
        }

        foreach (var e in run.Exceptions)
        {
            if (e.Type is ExceptionType.CapacityOverload or ExceptionType.NotPlanned) continue;   // shown elsewhere / undated
            var date = e.RelatedDate ?? cal.Start;
            // Overdue items sit on day one of the horizon; anything beyond the horizon is not shown.
            if (date < cal.Start) date = cal.Start;
            else if (!InHorizon(date)) continue;
            Add(date, CalendarEventKind.Exception, e.ItemCode, e.WarehouseCode, e.Quantity, e.Message, e.Type.ToString(),
                e.Type switch
                {
                    ExceptionType.Defer => "info",
                    ExceptionType.Expedite => "warn",
                    _ => "danger",
                });
        }

        foreach (var c in run.CapacityLoads.Where(c => c.Overloaded && InHorizon(c.BucketStart)))
            Add(c.BucketStart, CalendarEventKind.CapacityOverload, c.WorkCenterCode, null,
                Math.Round(c.RequiredHours - c.AvailableHours, 2),
                $"{c.WorkCenterCode} over capacity: {c.RequiredHours:0.##}h needed, {c.AvailableHours:0.##}h available",
                c.WorkCenterCode, "danger");
    }
}
