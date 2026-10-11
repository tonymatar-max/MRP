using CobaltMrp.Domain;

namespace CobaltMrp.Engine;

/// <summary>A forecast line after real sales orders have consumed part of it.</summary>
public sealed record NetForecast(Forecast Source, decimal Net);

/// <summary>
/// Forward forecast consumption. A forecast quantity is a stand-in for orders that have not arrived yet,
/// so once real sales orders exist inside a forecast period they replace that part of the forecast; counting
/// both would double the demand. A period runs from its start to the next forecast's start (the last one
/// reuses the previous spacing, 30 days if there is only one).
/// </summary>
public static class ForecastConsumption
{
    public static List<NetForecast> Net(PlanningDataSet data, MrpOptions opt, Func<string, bool> whAllowed)
    {
        var result = new List<NetForecast>();

        var salesByKey = data.Demand
            .Where(d => d.Source == DemandSource.SalesOrder && whAllowed(d.WarehouseCode))
            .GroupBy(d => (d.ItemCode, d.WarehouseCode), TupleComparer.Instance)
            .ToDictionary(g => g.Key, g => g.ToArray(), TupleComparer.Instance);

        var earliest = opt.HorizonStart.AddDays(-opt.ForecastLookbackDays);

        foreach (var g in data.Forecasts.Where(f => whAllowed(f.WarehouseCode))
                     .GroupBy(f => (f.ItemCode, f.WarehouseCode), TupleComparer.Instance))
        {
            // Period boundaries come from ALL of an item's forecasts, so filter by lookback only afterwards.
            var list = g.OrderBy(f => f.PeriodStart).ToList();
            salesByKey.TryGetValue(g.Key, out var sales);

            for (var i = 0; i < list.Count; i++)
            {
                var f = list[i];
                if (f.PeriodStart < earliest) continue;

                var consumedBySales = 0m;
                if (opt.ConsumeForecast && sales is not null)
                {
                    var start = f.PeriodStart;
                    var end = i + 1 < list.Count
                        ? list[i + 1].PeriodStart
                        : start.AddDays(i > 0 ? Math.Max(1, start.DayNumber - list[i - 1].PeriodStart.DayNumber) : 30);
                    consumedBySales = sales.Where(s => s.DueDate >= start && s.DueDate < end).Sum(s => s.Quantity);
                }

                var net = f.Quantity - f.Consumed - consumedBySales;
                if (net > 0) result.Add(new NetForecast(f, net));
            }
        }
        return result;
    }
}
