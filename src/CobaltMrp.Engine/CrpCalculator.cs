using CobaltMrp.Domain;

namespace CobaltMrp.Engine;

/// <summary>
/// Capacity Requirements Planning: converts planned production orders into work-center
/// load (setup + run hours from routings), placed in the bucket where the order is released,
/// and compares it with available capacity. Overloads also raise exception messages.
/// </summary>
public static class CrpCalculator
{
    public static void Compute(MrpRun run, PlanningDataSet data, BucketCalendar cal, WorkCalendar? workCalendar = null)
    {
        var centers = data.WorkCenters.ToDictionary(w => w.Code, StringComparer.OrdinalIgnoreCase);
        var routes = data.Routings
            .GroupBy(r => r.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Sequence).ToArray(),
                          StringComparer.OrdinalIgnoreCase);

        var load = new Dictionary<(string Wc, int Bucket), decimal>();

        foreach (var po in run.PlannedOrders.Where(p => p.Type == PlannedOrderType.ProductionOrder))
        {
            if (!routes.TryGetValue(po.ItemCode, out var ops)) continue;
            // Load lands in the bucket of the release date (past releases fold into bucket 0).
            var idx = cal.IndexOf(po.ReleaseDate);
            if (idx < 0) continue;

            foreach (var op in ops)
            {
                var hours = op.SetupHours + op.HoursPerUnit * po.Quantity;
                var k = (op.WorkCenterCode, idx);
                load[k] = load.GetValueOrDefault(k) + hours;
            }
        }

        foreach (var ((wc, bucket), required) in load.OrderBy(x => x.Key.Wc).ThenBy(x => x.Key.Bucket))
        {
            centers.TryGetValue(wc, out var center);
            // Only days the plant works add capacity: a 7-day bucket is ~5 working days, not 7.
            var daysInBucket = (workCalendar ?? WorkCalendar.AllDays).WorkingDays(cal.StartOf(bucket), run.BucketDays);
            var available = center is null
                ? 0m
                : center.DailyCapacityHours * center.EfficiencyFactor * daysInBucket;

            var cl = new CapacityLoad
            {
                WorkCenterCode = wc,
                BucketStart = cal.StartOf(bucket),
                RequiredHours = Math.Round(required, 2),
                AvailableHours = Math.Round(available, 2),
            };
            run.CapacityLoads.Add(cl);

            if (cl.Overloaded)
                run.Exceptions.Add(new ExceptionMessage
                {
                    Type = ExceptionType.CapacityOverload,
                    ItemCode = wc,
                    RelatedDate = cl.BucketStart,
                    Quantity = cl.RequiredHours - cl.AvailableHours,
                    Message = $"Work center {wc} needs {cl.RequiredHours:0.##}h but has " +
                              $"{cl.AvailableHours:0.##}h in the bucket from {cl.BucketStart:yyyy-MM-dd}.",
                });
        }
    }
}
