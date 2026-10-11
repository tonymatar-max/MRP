using CobaltMrp.Domain;

namespace CobaltMrp.Data.ServiceLayer;

/// <summary>Outcome of pushing one planned order to B1.</summary>
/// <param name="DocLines">How many planned orders share the SAP B1 document this one went into (1 = its own document).</param>
public sealed record WritebackResult(Guid PlannedOrderId, bool Success, int? DocEntry, string? Error, int DocLines = 1);

/// <summary>Aggregate outcome of a writeback batch.</summary>
public sealed class WritebackSummary
{
    public List<WritebackResult> Results { get; } = new();
    public IEnumerable<WritebackResult> Succeeded => Results.Where(r => r.Success);
    public IEnumerable<WritebackResult> Failed => Results.Where(r => !r.Success);
}

/// <summary>
/// Pushes firmed <see cref="PlannedOrder"/>s to SAP B1 through the Service Layer, collecting
/// per-order successes and failures so one bad order never blocks the rest.
/// </summary>
public sealed class PlannedOrderWriteback
{
    private readonly ServiceLayerClient _client;
    private readonly Func<PlannedOrder, string?> _vendorResolver;

    /// <param name="client">A logged-in (or loginable) Service Layer client.</param>
    /// <param name="vendorResolver">Returns the supplier CardCode for a purchase order
    /// (planned orders carry no vendor). Returning null/empty fails that order with a clear message.</param>
    public PlannedOrderWriteback(ServiceLayerClient client, Func<PlannedOrder, string?> vendorResolver)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _vendorResolver = vendorResolver ?? throw new ArgumentNullException(nameof(vendorResolver));
    }

    /// <summary>Several lines on one document at most (SAP B1 handles far more; this keeps one failing line easy to find).</summary>
    public const int MaxLinesPerDocument = 100;

    /// <summary>Push each firmed order; unfirmed orders and already-pushed orders are skipped.
    /// On success <see cref="PlannedOrder.B1DocEntry"/> is set on the order. With a <paramref name="grouping"/> other than None,
    /// purchase orders and requests for the same supplier (and transfers between the same warehouses) become ONE document with a
    /// line per order. If SAP B1 refuses a combined document, its orders are sent one by one so a single bad line cannot block the rest.</summary>
    public async Task<WritebackSummary> PushAsync(IEnumerable<PlannedOrder> orders, CancellationToken ct = default,
        WritebackSummary? into = null, DocumentGrouping grouping = DocumentGrouping.None)
    {
        var summary = into ?? new WritebackSummary();
        var pending = new List<PlannedOrder>();
        foreach (var order in orders)
        {
            ct.ThrowIfCancellationRequested();
            if (!order.Firmed)
            {
                summary.Results.Add(new(order.Id, false, null, "Order is not firmed."));
                continue;
            }
            if (!string.IsNullOrEmpty(order.B1DocEntry))
            {
                summary.Results.Add(new(order.Id, false, null, $"Already pushed as DocEntry {order.B1DocEntry}."));
                continue;
            }
            pending.Add(order);
        }

        foreach (var batch in Batches(pending, grouping))
        {
            ct.ThrowIfCancellationRequested();
            if (batch.Count > 1 && await TryPushCombinedAsync(batch, summary, ct)) continue;
            foreach (var order in batch)
            {
                ct.ThrowIfCancellationRequested();
                await PushOneAsync(order, summary, ct);
            }
        }
        return summary;
    }

    /// <summary>The orders that can share one document, in the order they were given. Production orders are never combined.</summary>
    internal List<List<PlannedOrder>> Batches(List<PlannedOrder> orders, DocumentGrouping grouping)
    {
        var map = new Dictionary<string, List<PlannedOrder>>();
        var result = new List<List<PlannedOrder>>();
        foreach (var o in orders)
        {
            var key = GroupKey(o, grouping);
            if (key is not null && map.TryGetValue(key, out var open) && open.Count < MaxLinesPerDocument) { open.Add(o); continue; }
            var list = new List<PlannedOrder> { o };
            result.Add(list);
            if (key is not null) map[key] = list;
        }
        return result;
    }

    private string? GroupKey(PlannedOrder o, DocumentGrouping grouping)
    {
        if (grouping == DocumentGrouping.None) return null;
        var date = grouping == DocumentGrouping.SupplierAndDate ? o.DueDate.ToString("yyyy-MM-dd") : "";
        switch (o.Type)
        {
            case PlannedOrderType.PurchaseOrder:
                var vendor = _vendorResolver(o);
                return string.IsNullOrWhiteSpace(vendor) ? null : $"PO|{vendor.Trim().ToUpperInvariant()}|{date}";      // no supplier: sent alone so its own error shows
            case PlannedOrderType.PurchaseRequest:
                return $"PR|{(_vendorResolver(o) ?? "").Trim().ToUpperInvariant()}|{date}";
            case PlannedOrderType.TransferRequest:
                // A transfer request has one header due date, so transfers only share a document when their dates match too.
                return string.IsNullOrWhiteSpace(o.FromWarehouse) ? null : $"TR|{o.FromWarehouse.ToUpperInvariant()}|{o.WarehouseCode.ToUpperInvariant()}|{o.DueDate:yyyy-MM-dd}";
            default:
                return null;
        }
    }

    private async Task<bool> TryPushCombinedAsync(List<PlannedOrder> batch, WritebackSummary summary, CancellationToken ct)
    {
        try
        {
            int docEntry;
            switch (batch[0].Type)
            {
                case PlannedOrderType.PurchaseOrder: docEntry = await _client.CreatePurchaseOrderAsync(batch, _vendorResolver(batch[0])!, ct); break;
                case PlannedOrderType.PurchaseRequest: docEntry = await _client.CreatePurchaseRequestAsync(batch, _vendorResolver(batch[0]), ct); break;
                case PlannedOrderType.TransferRequest: docEntry = await _client.CreateTransferRequestAsync(batch, ct); break;
                default: return false;
            }
            foreach (var o in batch)
            {
                o.B1DocEntry = docEntry.ToString();
                summary.Results.Add(new(o.Id, true, docEntry, null, batch.Count));
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        // SAP B1 answered with an error (nothing was created) or the payload was invalid: fall back to one document per order.
        catch (Exception ex) when (ex is ServiceLayerException or InvalidOperationException) { return false; }
    }

    private async Task PushOneAsync(PlannedOrder order, WritebackSummary summary, CancellationToken ct)
    {
        try
        {
            int docEntry;
            switch (order.Type)
            {
                case PlannedOrderType.PurchaseOrder:
                    var vendor = _vendorResolver(order);
                    if (string.IsNullOrWhiteSpace(vendor))
                        throw new InvalidOperationException($"No vendor resolved for item {order.ItemCode}.");
                    docEntry = await _client.CreatePurchaseOrderAsync(order, vendor, ct);
                    break;
                case PlannedOrderType.PurchaseRequest:
                    docEntry = await _client.CreatePurchaseRequestAsync(order, _vendorResolver(order), ct);
                    break;
                case PlannedOrderType.TransferRequest:
                    docEntry = await _client.CreateTransferRequestAsync(order, ct);
                    break;
                default:
                    docEntry = await _client.CreateProductionOrderAsync(order, ct);
                    break;
            }

            order.B1DocEntry = docEntry.ToString();
            summary.Results.Add(new(order.Id, true, docEntry, null));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            summary.Results.Add(new(order.Id, false, null, ex.Message));
        }
    }
}
