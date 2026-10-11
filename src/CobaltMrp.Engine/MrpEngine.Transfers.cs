using CobaltMrp.Domain;

namespace CobaltMrp.Engine;

/// <summary>
/// Warehouse-level transfers. For an item planned in several warehouses, a shortage in one warehouse can be covered by
/// stock another warehouse can spare instead of by a new purchase or production order.
///
/// How it works, per item:
///  1. DRY PASS: plan every warehouse on its own without creating anything real. This yields each warehouse's
///     shortages (the candidate orders and their net requirement) and its projected on-hand timeline WITHOUT any
///     new orders ("raw"), which is what the warehouse really holds and expects.
///  2. ALLOCATE: take the shortages earliest first. A source warehouse can spare, for a transfer leaving on a given
///     day, the smallest amount by which its raw projected on-hand exceeds its stock floor from that day to the end
///     of the plan (so the transfer never creates a shortage there), minus what earlier transfers already took.
///  3. REAL PASS: add each transfer as a scheduled receipt in the receiving warehouse and as demand in the sending
///     one, then plan normally. Orders shrink or vanish, and a production order's components are exploded only for
///     what is still to be made.
/// </summary>
public sealed partial class MrpEngine
{
    private sealed class PlanTrace
    {
        public decimal[] PohRaw;
        public decimal Floor;
        public readonly List<(PlannedOrder Order, decimal Net)> Orders = new();
        public PlanTrace(int buckets) { PohRaw = new decimal[buckets]; }
    }

    private sealed record TransferPlan(
        string From, string To, decimal Quantity, DateOnly Due, DateOnly Release, Guid DestOrderId, decimal DestNet);

    private (List<TransferPlan> Transfers, MrpRun Scratch) PlanTransfers(
        PlanItem item, IReadOnlyList<string> warehouses, BucketCalendar cal, MrpOptions opt,
        Dictionary<(string, string), List<DemandEntry>> independent,
        Dictionary<(string, string), List<DemandEntry>> dependent,
        Dictionary<(string, string), SupplyOrder[]> receipts,
        Dictionary<(string, string), decimal> onHand,
        Dictionary<string, BomComponent[]> bomByParent,
        Dictionary<string, PlanItem> itemByCode,
        Dictionary<string, HashSet<string>> dependentWh,
        WorkCalendar workCalendar)
    {
        var scratch = new MrpRun();
        var traces = new Dictionary<string, PlanTrace>(StringComparer.OrdinalIgnoreCase);
        foreach (var wh in warehouses)
        {
            var trace = new PlanTrace(cal.Count);
            PlanItemWarehouse(item, wh, cal, opt, scratch, independent, dependent, receipts, onHand,
                bomByParent, itemByCode, dependentWh, workCalendar, trace, dry: true);
            traces[wh] = trace;
        }

        var allowedSources = opt.TransferSources.Count == 0
            ? new HashSet<string>(warehouses, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(opt.TransferSources, StringComparer.OrdinalIgnoreCase);
        var sources = warehouses.Where(allowedSources.Contains).ToList();
        var used = sources.ToDictionary(s => s, _ => new decimal[cal.Count], StringComparer.OrdinalIgnoreCase);

        decimal Available(string source, int fromBucket)
        {
            var t = traces[source];
            var best = decimal.MaxValue;
            for (var b = Math.Max(0, fromBucket); b < cal.Count; b++)
                best = Math.Min(best, t.PohRaw[b] - t.Floor - used[source][b]);
            return best == decimal.MaxValue ? 0m : best;
        }

        var transfers = new List<TransferPlan>();
        var shortages = traces
            .SelectMany(kv => kv.Value.Orders.Select(o => (Wh: kv.Key, o.Order, o.Net)))
            .OrderBy(x => x.Order.DueDate).ThenBy(x => x.Wh, StringComparer.OrdinalIgnoreCase);

        foreach (var (dest, order, net) in shortages)
        {
            var remaining = net;
            var release = workCalendar.OnOrBeforeWorkingDay(order.DueDate.AddDays(-opt.TransferLeadDays));
            var releaseBucket = Math.Max(0, cal.IndexOf(release));

            // Largest spare quantity first: fewest transfers for the same shortage.
            foreach (var src in sources.Where(s => !string.Equals(s, dest, StringComparison.OrdinalIgnoreCase))
                                       .OrderByDescending(s => Available(s, releaseBucket)).ThenBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                if (remaining <= 0) break;
                var take = Math.Min(remaining, Available(src, releaseBucket));
                if (take <= 0) continue;
                for (var b = releaseBucket; b < cal.Count; b++) used[src][b] += take;
                remaining -= take;
                transfers.Add(new TransferPlan(src, dest, take, order.DueDate, release, order.Id, net));
            }
        }
        return (transfers, scratch);
    }

    /// <summary>Turn the decided transfers into receipts at the destination and demand at the source for the real pass.</summary>
    private static void ApplyTransfers(
        PlanItem item, IReadOnlyList<TransferPlan> transfers, BucketCalendar cal,
        Dictionary<(string, string), List<DemandEntry>> independent,
        Dictionary<(string, string), SupplyOrder[]> receipts)
    {
        foreach (var t in transfers)
        {
            var inKey = (item.ItemCode, t.To);
            var current = receipts.TryGetValue(inKey, out var existing) ? existing : Array.Empty<SupplyOrder>();
            receipts[inKey] = current.Append(new SupplyOrder
            {
                ItemCode = item.ItemCode, WarehouseCode = t.To, Type = SupplyType.TransferIn,
                Quantity = t.Quantity, DueDate = t.Due, SourceDocKey = null,
            }).ToArray();

            // The sending warehouse loses the stock on the day the request goes out (never before the plan starts).
            AddDependent(independent, (item.ItemCode, t.From),
                new DemandEntry(DemandSource.TransferOut, t.Quantity, t.Release < cal.Start ? cal.Start : t.Release, "TRANSFER", null));
        }
    }

    private static void AddTransferOrders(
        MrpRun run, PlanItem item, IReadOnlyList<TransferPlan> transfers, MrpRun scratch, BucketCalendar cal, MrpOptions opt)
    {
        foreach (var t in transfers)
        {
            var order = new PlannedOrder
            {
                ItemCode = item.ItemCode, WarehouseCode = t.To, FromWarehouse = t.From,
                Type = PlannedOrderType.TransferRequest, Quantity = t.Quantity,
                DueDate = t.Due, ReleaseDate = t.Release, LowLevelCode = item.LowLevelCode,
                LeadTimeDays = opt.TransferLeadDays, LeadTimeSource = "Transfer",
            };
            run.PlannedOrders.Add(order);

            // Pegging: the demand that caused the shortage this transfer covers, in proportion.
            var factor = t.DestNet > 0 ? Math.Min(1m, t.Quantity / t.DestNet) : 1m;
            var links = scratch.Pegging.Where(p => p.PlannedOrderId == t.DestOrderId).ToList();
            if (links.Count == 0)
                run.Pegging.Add(new PeggingLink { PlannedOrderId = order.Id, DemandSource = DemandSource.SafetyStock, Quantity = t.Quantity, DemandDueDate = t.Due });
            foreach (var p in links)
                run.Pegging.Add(new PeggingLink
                {
                    PlannedOrderId = order.Id, DemandSource = p.DemandSource, Quantity = p.Quantity * factor,
                    DemandDueDate = p.DemandDueDate, DemandDocKey = p.DemandDocKey, ParentItemCode = p.ParentItemCode,
                });

            if (t.Release < cal.Start)
                run.Exceptions.Add(new ExceptionMessage
                {
                    Type = ExceptionType.ReleaseInPast, ItemCode = item.ItemCode, WarehouseCode = t.To,
                    RelatedDate = t.Release, Quantity = t.Quantity, PlannedOrderId = order.Id,
                    Message = $"Transfer of {t.Quantity:0.##} from {t.From} to {t.To} should have been requested {t.Release:yyyy-MM-dd} " +
                              $"(before plan start) to arrive by {t.Due:yyyy-MM-dd}.",
                });
        }
    }
}
