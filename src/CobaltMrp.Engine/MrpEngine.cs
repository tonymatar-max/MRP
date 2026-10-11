using CobaltMrp.Domain;

namespace CobaltMrp.Engine;

/// <summary>
/// The core regenerative MRP engine. Pure and deterministic: given a frozen
/// <see cref="PlanningDataSet"/> and <see cref="MrpOptions"/> it produces a fully
/// pegged <see cref="MrpRun"/> (planned orders, pegging, exceptions, capacity load).
///
/// Field interpretation by planning method:
///   MRP          — <see cref="PlanItem.SafetyStock"/> is a continuous floor.
///   ReorderPoint — order when projected-on-hand falls to <see cref="PlanItem.ReorderPoint"/>.
///   MinMax       — min level = <see cref="PlanItem.ReorderPoint"/>, max level = <see cref="PlanItem.MaxStockLevel"/> (or MaxOrderQty if unset).
/// </summary>
public sealed partial class MrpEngine
{
    private sealed record DemandEntry(
        DemandSource Source, decimal Quantity, DateOnly DueDate,
        string? DocKey, string? ParentItemCode);

    public MrpRun Run(PlanningDataSet data, MrpOptions opt)
    {
        if (!opt.CompanyLevel) return RunCore(data, opt);

        // Company level: pool the warehouses, plan once, then attach each order to the warehouse that receives it.
        var (pooled, receiving) = CompanyLevel.Collapse(data, opt);
        var run = RunCore(pooled, CompanyLevel.PooledOptions(opt));
        CompanyLevel.Remap(run, receiving);
        return run;
    }

    private MrpRun RunCore(PlanningDataSet data, MrpOptions opt)
    {
        var cal = new BucketCalendar(opt.HorizonStart, opt.HorizonDays, opt.BucketDays);
        var run = new MrpRun
        {
            HorizonStart = cal.Start,
            HorizonEnd = cal.End,
            BucketDays = opt.BucketDays,
            RunBy = opt.RunBy,
        };

        LowLevelCoder.Assign(data.Items, data.Bom);

        var whFilter = new HashSet<string>(opt.Warehouses, StringComparer.OrdinalIgnoreCase);
        bool WhAllowed(string wh) => whFilter.Count == 0 || whFilter.Contains(wh);

        var itemByCode = data.Items.ToDictionary(i => i.ItemCode, StringComparer.OrdinalIgnoreCase);
        var bomByParent = data.Bom
            .GroupBy(b => b.ParentItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);

        // Independent demand (sales orders) keyed by (item, wh).
        var workCalendar = data.Calendar ?? WorkCalendar.AllDays;
        var netForecasts = opt.UseForecasts ? ForecastConsumption.Net(data, opt, WhAllowed) : new List<NetForecast>();
        var independent = GroupDemand(data, WhAllowed, netForecasts);

        // Dependent demand accumulated as parents are planned (components come later by LLC).
        var dependent = new Dictionary<(string, string), List<DemandEntry>>(TupleComparer.Instance);

        // Scheduled receipts (open PO/prod orders) keyed by (item, wh).
        var receipts = data.Supply
            .Where(s => WhAllowed(s.WarehouseCode))
            .GroupBy(s => (s.ItemCode, s.WarehouseCode), TupleComparer.Instance)
            .ToDictionary(g => g.Key, g => g.ToArray(), TupleComparer.Instance);

        // On-hand by (item, wh).
        var onHand = data.Stock
            .Where(s => WhAllowed(s.WarehouseCode) && s.PlanningEnabled)
            .GroupBy(s => (s.ItemCode, s.WarehouseCode), TupleComparer.Instance)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.OnHand), TupleComparer.Instance);

        // Which warehouses each item must be planned in.
        var (itemWarehouses, stockLocations) = BuildItemWarehouseMap(data, independent, receipts, onHand, WhAllowed);
        // Warehouses that receive dependent demand while planning (components reached via BOM explosion).
        var dependentWh = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Demand/supply on items that are not in the plan would otherwise vanish silently.
        foreach (var g in independent.Where(kv => !itemByCode.ContainsKey(kv.Key.Item1))
                                     .GroupBy(kv => kv.Key.Item1, StringComparer.OrdinalIgnoreCase))
        {
            var lines = g.Sum(kv => kv.Value.Count);
            var qty = g.Sum(kv => kv.Value.Sum(e => e.Quantity));
            run.Exceptions.Add(new ExceptionMessage
            {
                Type = ExceptionType.NotPlanned, ItemCode = g.Key, Quantity = qty,
                Message = $"{lines} demand line(s) totalling {qty:0.##} ignored: the item is not in the plan " +
                          "(inactive, frozen or non-inventory).",
            });
        }
        foreach (var g in receipts.Where(kv => !itemByCode.ContainsKey(kv.Key.Item1))
                                  .GroupBy(kv => kv.Key.Item1, StringComparer.OrdinalIgnoreCase))
        {
            var qty = g.Sum(kv => kv.Value.Sum(r => r.Quantity));
            run.Exceptions.Add(new ExceptionMessage
            {
                Type = ExceptionType.NotPlanned, ItemCode = g.Key, Quantity = qty,
                Message = $"Open supply of {qty:0.##} ignored: the item is not in the plan.",
            });
        }

        // Plan strictly in ascending low-level-code order so dependent demand is known in time.
        foreach (var item in data.Items.OrderBy(i => i.LowLevelCode).ThenBy(i => i.ItemCode))
        {
            if (item.PlanningMethod == PlanningMethod.None) continue;

            var warehouses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (itemWarehouses.TryGetValue(item.ItemCode, out var active)) warehouses.UnionWith(active);
            if (dependentWh.TryGetValue(item.ItemCode, out var dep)) warehouses.UnionWith(dep);

            if (warehouses.Count == 0)
            {
                // Item-level policy (min/max, reorder point, safety stock) says "keep stock", but the item
                // has no stock, demand or supply anywhere. B1 creates a stock row in EVERY warehouse, so
                // planning them all would invent one order per warehouse. Pick an unambiguous location.
                stockLocations.TryGetValue(item.ItemCode, out var rows);
                if (!string.IsNullOrWhiteSpace(item.HomeWarehouse) && WhAllowed(item.HomeWarehouse))
                    warehouses.Add(item.HomeWarehouse);
                else if (rows is { Count: 1 })
                    warehouses.Add(rows.First());
                else if (rows is { Count: > 1 } &&
                         (item.SafetyStock > 0 || item.ReorderPoint > 0 || item.PlanningMethod == PlanningMethod.MinMax))
                    run.Exceptions.Add(new ExceptionMessage
                    {
                        Type = ExceptionType.NotPlanned, ItemCode = item.ItemCode,
                        Message = $"Has a stock policy but no stock, demand or supply in any of {rows.Count} warehouses " +
                                  "and no default warehouse; nothing planned. Set a default warehouse.",
                    });
            }

            var whList = warehouses.OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();

            // Warehouse level with transfers: first learn who is short and who can spare stock, then plan for real.
            List<TransferPlan>? transfers = null;
            MrpRun? scratch = null;
            if (opt.TransferEnabled && whList.Count >= 2)
            {
                (transfers, scratch) = PlanTransfers(item, whList, cal, opt, independent, dependent, receipts, onHand,
                    bomByParent, itemByCode, dependentWh, workCalendar);
                if (transfers.Count > 0) ApplyTransfers(item, transfers, cal, independent, receipts);
            }

            foreach (var wh in whList)
            {
                PlanItemWarehouse(
                    item, wh, cal, opt, run,
                    independent, dependent, receipts, onHand,
                    bomByParent, itemByCode, dependentWh, workCalendar);
            }

            if (transfers is { Count: > 0 }) AddTransferOrders(run, item, transfers, scratch!, cal, opt);
        }

        // Keep only the selected items' results; capacity and the calendar are computed from what is kept.
        if (opt.ItemScope is { } scope)
        {
            var dropped = run.PlannedOrders.Where(o => !scope.Contains(o.ItemCode)).Select(o => o.Id).ToHashSet();
            run.PlannedOrders.RemoveAll(o => dropped.Contains(o.Id));
            run.Pegging.RemoveAll(p => dropped.Contains(p.PlannedOrderId));
            run.Exceptions.RemoveAll(e => !scope.Contains(e.ItemCode));
        }

        if (opt.RunCapacityPlanning)
            CrpCalculator.Compute(run, data, cal, workCalendar);

        CalendarEventBuilder.Build(run, data, netForecasts, WhAllowed, cal, workCalendar);

        return run;
    }

    private void PlanItemWarehouse(
        PlanItem item, string wh, BucketCalendar cal, MrpOptions opt, MrpRun run,
        Dictionary<(string, string), List<DemandEntry>> independent,
        Dictionary<(string, string), List<DemandEntry>> dependent,
        Dictionary<(string, string), SupplyOrder[]> receipts,
        Dictionary<(string, string), decimal> onHand,
        Dictionary<string, BomComponent[]> bomByParent,
        Dictionary<string, PlanItem> itemByCode,
        Dictionary<string, HashSet<string>> dependentWh,
        WorkCalendar workCalendar,
        PlanTrace? trace = null, bool dry = false)
    {
        var key = (item.ItemCode, wh);

        // Bucketed gross requirements, carrying the entries for pegging + past-due detection.
        var reqByBucket = new List<DemandEntry>[cal.Count];
        var recByBucket = new decimal[cal.Count];
        for (int b = 0; b < cal.Count; b++) reqByBucket[b] = new List<DemandEntry>();

        // Past-due demand is folded into the first bucket and reported ONCE per item/warehouse
        // (a legacy company can carry thousands of overdue lines; one message each is unusable).
        int pastDueLines = 0; decimal pastDueQty = 0; DateOnly? oldestPastDue = null;

        void AddDemand(IEnumerable<DemandEntry> entries)
        {
            foreach (var e in entries)
            {
                if (e.DueDate < cal.Start)
                {
                    pastDueLines++; pastDueQty += e.Quantity;
                    if (oldestPastDue is null || e.DueDate < oldestPastDue) oldestPastDue = e.DueDate;
                }
                var idx = cal.IndexOf(e.DueDate);
                if (idx >= 0) reqByBucket[idx].Add(e);
            }
        }

        if (independent.TryGetValue(key, out var ind)) AddDemand(ind);
        if (dependent.TryGetValue(key, out var dep)) AddDemand(dep);

        if (pastDueLines > 0)
            run.Exceptions.Add(new ExceptionMessage
            {
                Type = ExceptionType.PastDue, ItemCode = item.ItemCode, WarehouseCode = wh,
                RelatedDate = oldestPastDue, Quantity = pastDueQty,
                Message = pastDueLines == 1
                    ? $"Demand of {pastDueQty:0.##} is past due ({oldestPastDue:yyyy-MM-dd})."
                    : $"{pastDueLines} demand lines totalling {pastDueQty:0.##} are past due " +
                      $"(oldest {oldestPastDue:yyyy-MM-dd}); planned as due now.",
            });

        // Scheduled receipts into buckets.
        var scheduled = receipts.TryGetValue(key, out var recs) ? recs : Array.Empty<SupplyOrder>();
        foreach (var r in scheduled)
        {
            var idx = cal.IndexOf(r.DueDate);
            if (idx >= 0) recByBucket[idx] += r.Quantity;
        }
        decimal futureReceiptQty = scheduled.Where(r => cal.IndexOf(r.DueDate) >= 0).Sum(r => r.Quantity);

        decimal poh = onHand.TryGetValue(key, out var oh) ? oh : 0m;
        decimal pohRaw = poh;     // projected on hand WITHOUT any new order: what the warehouse really holds and expects
        bool isMrp = item.PlanningMethod == PlanningMethod.Mrp;

        for (int b = 0; b < cal.Count; b++)
        {
            decimal requirement = reqByBucket[b].Sum(e => e.Quantity);
            decimal pohAfter = poh + recByBucket[b] - requirement;
            pohRaw += recByBucket[b] - requirement;
            if (trace is not null) trace.PohRaw[b] = pohRaw;

            // Determine the level we must not drop below and the level to replenish up to.
            decimal floor = item.PlanningMethod switch
            {
                PlanningMethod.Mrp => item.SafetyStock,
                PlanningMethod.ReorderPoint => item.ReorderPoint,
                PlanningMethod.MinMax => item.ReorderPoint, // min level
                _ => 0m,
            };

            if (trace is not null) trace.Floor = floor;

            if (pohAfter < floor || (requirement > 0 && pohAfter < 0))
            {
                decimal net = item.PlanningMethod == PlanningMethod.MinMax
                    ? Math.Max(item.MaxStockLevel > 0 ? item.MaxStockLevel : item.MaxOrderQty, floor) - pohAfter      // order up to max level
                    : floor - pohAfter;                                  // cover to floor

                // Period-of-supply: pull forward net requirements inside the supply window.
                if (item.LotSizing == LotSizingRule.PeriodOfSupply && item.PeriodOfSupplyDays > 0 && isMrp)
                    net += LookaheadNet(cal, reqByBucket, recByBucket, b, item.PeriodOfSupplyDays);

                // A maximum order quantity caps ONE order, not the shortage: keep ordering in this bucket until the
                // whole requirement is covered (a bounded number of times), instead of pushing the rest to later days.
                var sizes = new List<decimal>();
                for (var left = net; left > 0 && sizes.Count < 500;)
                {
                    var q = LotSizer.Size(item, left);
                    if (q <= 0) break;
                    sizes.Add(q);
                    left -= q;
                    if (item.MaxOrderQty <= 0) break;
                }
                if (sizes.Count == 0) { poh = pohAfter; continue; }
                var totalQty = sizes.Sum();

                var due = cal.StartOf(b);
                // Never recommend releasing on a day the plant is closed: step back to the previous working day.
                var release = workCalendar.OnOrBeforeWorkingDay(due.AddDays(-item.LeadTimeDays));
                var firstOrder = true;
                foreach (var qty in sizes)
                {
                var po = new PlannedOrder
                {
                    ItemCode = item.ItemCode, WarehouseCode = wh,
                    Type = item.Procurement == ProcurementType.Make
                        ? PlannedOrderType.ProductionOrder
                        : opt.PurchaseDocument == PurchaseDocument.Request ? PlannedOrderType.PurchaseRequest : PlannedOrderType.PurchaseOrder,
                    Quantity = qty, DueDate = due, ReleaseDate = release,
                    LowLevelCode = item.LowLevelCode,
                    VendorCode = item.Procurement == ProcurementType.Buy ? item.PreferredVendor : null,
                    VendorSource = item.Procurement == ProcurementType.Buy && item.PreferredVendor is not null ? item.SupplierSource : null,
                    LeadTimeDays = item.LeadTimeDays,
                    LeadTimeSource = item.LeadTimeSource,
                };
                run.PlannedOrders.Add(po);
                if (firstOrder) trace?.Orders.Add((po, net));
                var share = qty / totalQty;     // 1 unless the order was split by the maximum order quantity

                // Pegging: this order exists to satisfy the demand present in this bucket.
                foreach (var e in reqByBucket[b])
                    run.Pegging.Add(new PeggingLink
                    {
                        PlannedOrderId = po.Id, DemandSource = e.Source, Quantity = e.Quantity * share,
                        DemandDueDate = e.DueDate, DemandDocKey = e.DocKey, ParentItemCode = e.ParentItemCode,
                    });
                if (reqByBucket[b].Count == 0)
                    run.Pegging.Add(new PeggingLink
                    {
                        PlannedOrderId = po.Id, DemandSource = DemandSource.SafetyStock,
                        Quantity = qty, DemandDueDate = due,
                    });

                // Exception: order must be released before the horizon starts.
                if (release < cal.Start)
                    run.Exceptions.Add(new ExceptionMessage
                    {
                        Type = ExceptionType.ReleaseInPast, ItemCode = item.ItemCode, WarehouseCode = wh,
                        RelatedDate = release, Quantity = qty, PlannedOrderId = po.Id,
                        Message = $"Order of {qty:0.##} must be released {release:yyyy-MM-dd} " +
                                  $"(before plan start) to meet demand on {due:yyyy-MM-dd}.",
                    });

                // Expedite: an existing receipt arriving later could cover this deficit if pulled in.
                if (firstOrder && futureReceiptQty > 0)
                {
                    var late = scheduled
                        .Where(r => r.Type != SupplyType.TransferIn && cal.IndexOf(r.DueDate) > b)
                        .OrderBy(r => r.DueDate)
                        .FirstOrDefault();
                    if (late is not null)
                        run.Exceptions.Add(new ExceptionMessage
                        {
                            Type = ExceptionType.Expedite, ItemCode = item.ItemCode, WarehouseCode = wh,
                            RelatedDate = due, Quantity = late.Quantity, SourceDocKey = late.SourceDocKey,
                            SourceLine = late.SourceLine, CurrentDate = late.DueDate,
                            ProposedDate = due < cal.Start ? cal.Start : due, PlannedOrderId = po.Id,
                            Message = $"Consider expediting {late.Type} {late.SourceDocKey} " +
                                      $"from {late.DueDate:yyyy-MM-dd} to {due:yyyy-MM-dd}.",
                        });
                }

                // Explode a production order into dependent demand on its components.
                if (!dry && po.Type == PlannedOrderType.ProductionOrder &&
                    bomByParent.TryGetValue(item.ItemCode, out var comps))
                {
                    foreach (var c in comps)
                    {
                        var compWh = c.WarehouseCode ?? wh;
                        var qtyPer = c.QuantityPer * (1 + c.ScrapFactor);
                        var entry = new DemandEntry(
                            DemandSource.DependentDemand, qty * qtyPer, release,
                            $"PLN:{po.Id:N}", item.ItemCode);
                        AddDependent(dependent, (c.ComponentItemCode, compWh), entry);
                        if (!dependentWh.TryGetValue(c.ComponentItemCode, out var dw))
                            dependentWh[c.ComponentItemCode] = dw = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        dw.Add(compWh);
                    }
                }
                firstOrder = false;
                }

                poh = pohAfter + totalQty;
            }
            else
            {
                poh = pohAfter;
            }
        }

        // Defer/Cancel: receipts that were never needed (ended horizon with surplus over floor).
        decimal endFloor = item.PlanningMethod == PlanningMethod.Mrp ? item.SafetyStock : item.ReorderPoint;
        var realScheduled = scheduled.Where(r => r.Type != SupplyType.TransferIn).ToArray();   // a planned transfer is never "deferred"
        if (realScheduled.Length > 0 && poh - endFloor >= realScheduled.Min(r => r.Quantity))
        {
            var latest = realScheduled.OrderByDescending(r => r.DueDate).First();
            // The same order line already has an Expedite (it arrives later than needed): telling the planner to pull
            // it in AND push it out would be contradictory. The surplus is only there because MRP covered the early
            // shortage with a new order, so leave the decision to the Expedite message.
            var alreadyExpedited = run.Exceptions.Any(x => x.Type == ExceptionType.Expedite &&
                string.Equals(x.ItemCode, item.ItemCode, StringComparison.OrdinalIgnoreCase) &&
                x.WarehouseCode == wh && x.SourceDocKey == latest.SourceDocKey && x.SourceLine == latest.SourceLine);
            if (!alreadyExpedited)
            run.Exceptions.Add(new ExceptionMessage
            {
                Type = ExceptionType.Defer, ItemCode = item.ItemCode, WarehouseCode = wh,
                RelatedDate = latest.DueDate, Quantity = latest.Quantity, SourceDocKey = latest.SourceDocKey,
                SourceLine = latest.SourceLine, CurrentDate = latest.DueDate,
                // Pushing it past the end of the plan is the only date the plan itself can justify.
                ProposedDate = latest.DueDate < cal.End ? cal.End : null,
                Message = $"Projected surplus of {poh - endFloor:0.##}; consider deferring or cancelling " +
                          $"{latest.Type} {latest.SourceDocKey}.",
            });
        }
    }

    private static decimal LookaheadNet(
        BucketCalendar cal, List<DemandEntry>[] req, decimal[] rec, int from, int posDays)
    {
        var windowEnd = cal.StartOf(from).AddDays(posDays);
        decimal extra = 0;
        for (int j = from + 1; j < cal.Count && cal.StartOf(j) <= windowEnd; j++)
            extra += req[j].Sum(e => e.Quantity) - rec[j];
        return Math.Max(0, extra);
    }

    private static Dictionary<(string, string), List<DemandEntry>> GroupDemand(
        PlanningDataSet data, Func<string, bool> whAllowed, IReadOnlyList<NetForecast> netForecasts)
    {
        var map = new Dictionary<(string, string), List<DemandEntry>>(TupleComparer.Instance);

        foreach (var d in data.Demand.Where(d => whAllowed(d.WarehouseCode)))
            AddDependent(map, (d.ItemCode, d.WarehouseCode),
                new DemandEntry(d.Source, d.Quantity, d.DueDate, d.SourceDocKey, null));

        // Forecast periods that have already started were dropped and sales orders netted off inside
        // ForecastConsumption, so what is left is genuinely still-to-come demand.
        foreach (var f in netForecasts)
            AddDependent(map, (f.Source.ItemCode, f.Source.WarehouseCode),
                new DemandEntry(DemandSource.Forecast, f.Net, f.Source.PeriodStart, "FCST", null));

        return map;
    }

    /// <summary>
    /// Per item: the warehouses with an actual reason to plan (stock on hand/committed/ordered, demand
    /// or scheduled supply), plus every warehouse that merely has a stock row (used only as a fallback).
    /// </summary>
    private static (Dictionary<string, HashSet<string>> Active, Dictionary<string, HashSet<string>> StockRows)
        BuildItemWarehouseMap(
            PlanningDataSet data,
            Dictionary<(string, string), List<DemandEntry>> independent,
            Dictionary<(string, string), SupplyOrder[]> receipts,
            Dictionary<(string, string), decimal> onHand,
            Func<string, bool> whAllowed)
    {
        var active = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var rows = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(Dictionary<string, HashSet<string>> map, string item, string wh)
        {
            if (!whAllowed(wh)) return;
            if (!map.TryGetValue(item, out var set))
                map[item] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(wh);
        }
        foreach (var s in data.Stock.Where(s => s.PlanningEnabled))
        {
            Add(rows, s.ItemCode, s.WarehouseCode);
            if (s.OnHand != 0 || s.Committed != 0 || s.Ordered != 0) Add(active, s.ItemCode, s.WarehouseCode);
        }
        foreach (var k in independent.Keys) Add(active, k.Item1, k.Item2);
        foreach (var k in receipts.Keys) Add(active, k.Item1, k.Item2);
        foreach (var kv in onHand.Where(kv => kv.Value != 0)) Add(active, kv.Key.Item1, kv.Key.Item2);
        return (active, rows);
    }

    private static void AddDependent(
        Dictionary<(string, string), List<DemandEntry>> map, (string, string) key, DemandEntry e)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<DemandEntry>();
        list.Add(e);
    }
}
