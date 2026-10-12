using CobaltMrp.Domain;

namespace CobaltMrp.Data.ServiceLayer;

/// <summary>Outcome of pushing one planned order to B1.</summary>
/// <param name="DocLines">How many planned orders share the SAP B1 document this one went into (1 = its own document).</param>
/// <param name="Error">Why it failed, or null.</param>
/// <param name="AlreadyPushed">The order already had its SAP B1 document from an earlier release: nothing was sent and it is not a failure.</param>
/// <param name="Adopted">A document for this order was already in SAP B1 (an earlier attempt whose answer was lost); it was adopted instead of posting again.</param>
/// <param name="OutcomeUnknown">A connection problem left it unknown whether SAP B1 created the document; a later release looks for it before posting.</param>
public sealed record WritebackResult(Guid PlannedOrderId, bool Success, int? DocEntry, string? Error, int DocLines = 1,
    bool AlreadyPushed = false, bool Adopted = false, bool OutcomeUnknown = false);

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

    /// <summary>After this many connection failures in a row the rest of the release is not attempted (it would only time out
    /// again); those orders are reported as "not sent" and a later release sends them.</summary>
    public const int MaxConsecutiveTransportFailures = 3;

    private sealed class RunState { public int TransportFailures; }

    /// <summary>Push each firmed order; unfirmed orders and already-pushed orders are skipped.
    /// On success <see cref="PlannedOrder.B1DocEntry"/> is set on the order. With a <paramref name="grouping"/> other than None,
    /// purchase orders and requests for the same supplier (and transfers between the same warehouses) become ONE document with a
    /// line per order. If SAP B1 refuses a combined document, the batch is split in halves repeatedly until the bad line(s) are
    /// isolated, so the good lines still share documents. A connection error never aborts the other documents: it is reported per
    /// order, and because SAP B1 may have created the document anyway, SAP B1 is searched for it (by the planned-order tag in the
    /// remarks) before anything is posted again.</summary>
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
                summary.Results.Add(new(order.Id, false, null, $"Already pushed as DocEntry {order.B1DocEntry}.", AlreadyPushed: true));
                continue;
            }
            pending.Add(order);
        }

        var state = new RunState();
        foreach (var batch in Batches(pending, grouping))
        {
            ct.ThrowIfCancellationRequested();
            await PushBatchAsync(batch, summary, state, top: true, ct);
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

    // ---- One batch (one intended document) --------------------------------------------------------------------------

    private async Task PushBatchAsync(List<PlannedOrder> batch, WritebackSummary summary, RunState state, bool top, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        if (state.TransportFailures >= MaxConsecutiveTransportFailures)
        {
            foreach (var o in batch)
                summary.Results.Add(new(o.Id, false, null, "Not sent: the connection to SAP B1 failed several times in a row earlier in this release. Release again once SAP B1 is reachable."));
            return;
        }

        // Before posting anything, see whether SAP B1 already holds a document for these orders (an earlier attempt whose
        // answer was lost, or a release that stopped before Cobalt saved the document number). Never blocks a push.
        if (top)
        {
            var (looked, found) = await TryFindAsync(batch, ct);
            if (looked && found.Count > 0)
            {
                Adopt(batch, found, summary, "found in SAP B1");
                batch = batch.Where(o => !found.ContainsKey(o.Id)).ToList();
                if (batch.Count == 0) return;
            }
        }

        try
        {
            var docEntry = await PostAsync(batch, ct);
            state.TransportFailures = 0;
            foreach (var o in batch)
            {
                o.B1DocEntry = docEntry.ToString();
                summary.Results.Add(new(o.Id, true, docEntry, null, batch.Count));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ServiceLayerOutcomeUnknownException ex)
        {
            await HandleUnknownAsync(batch, ex.Message, summary, state, ct);
        }
        catch (Exception ex) when (ex is ServiceLayerException or InvalidOperationException)
        {
            // SAP B1 answered with an error (nothing was created) or the payload was invalid.
            state.TransportFailures = 0;
            if (batch.Count == 1)
            {
                summary.Results.Add(new(batch[0].Id, false, null, ex.Message));
                return;
            }
            // Bisect: the bad line(s) end up alone with their own message, every good line is still combined.
            var mid = batch.Count / 2;
            await PushBatchAsync(batch.Take(mid).ToList(), summary, state, top: false, ct);
            await PushBatchAsync(batch.Skip(mid).ToList(), summary, state, top: false, ct);
        }
        catch (Exception ex)
        {
            // Connection dropped, timeout, unreadable answer: SAP B1 may or may not have created the document.
            await HandleUnknownAsync(batch, Describe(ex), summary, state, ct);
        }
    }

    private async Task<int> PostAsync(List<PlannedOrder> batch, CancellationToken ct)
    {
        var first = batch[0];
        switch (first.Type)
        {
            case PlannedOrderType.PurchaseOrder:
                var vendor = _vendorResolver(first);
                if (string.IsNullOrWhiteSpace(vendor))
                    throw new InvalidOperationException($"No vendor resolved for item {first.ItemCode}.");
                return await _client.CreatePurchaseOrderAsync(batch, vendor, ct);
            case PlannedOrderType.PurchaseRequest:
                return await _client.CreatePurchaseRequestAsync(batch, _vendorResolver(first), ct);
            case PlannedOrderType.TransferRequest:
                return await _client.CreateTransferRequestAsync(batch, ct);
            default:
                if (batch.Count > 1) throw new InvalidOperationException("Production orders are created one by one.");
                return await _client.CreateProductionOrderAsync(first, ct);
        }
    }

    /// <summary>A search that never throws: (false, empty) when SAP B1 could not be asked.</summary>
    private async Task<(bool Looked, Dictionary<Guid, int> Found)> TryFindAsync(List<PlannedOrder> batch, CancellationToken ct)
    {
        try { return (true, await _client.FindExistingAsync(batch, ct)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return (false, new Dictionary<Guid, int>()); }
    }

    private static void Adopt(List<PlannedOrder> batch, Dictionary<Guid, int> found, WritebackSummary summary, string how)
    {
        var adopted = batch.Where(o => found.ContainsKey(o.Id)).ToList();
        foreach (var o in adopted)
        {
            var entry = found[o.Id];
            o.B1DocEntry = entry.ToString();
            summary.Results.Add(new(o.Id, true, entry, null, adopted.Count(x => found[x.Id] == entry), Adopted: true));
        }
    }

    private async Task HandleUnknownAsync(List<PlannedOrder> batch, string reason, WritebackSummary summary, RunState state, CancellationToken ct)
    {
        state.TransportFailures++;
        reason = reason.TrimEnd().TrimEnd('.') + ".";
        var (looked, found) = await TryFindAsync(batch, ct);
        if (looked && found.Count > 0)
        {
            Adopt(batch, found, summary, "found in SAP B1 after a connection error");
            batch = batch.Where(o => !found.ContainsKey(o.Id)).ToList();
        }
        foreach (var o in batch)
        {
            summary.Results.Add(looked
                ? new(o.Id, false, null, $"{reason} Checked SAP B1 afterwards: no document exists for this order, so it was not created. Release again.", batch.Count)
                : new(o.Id, false, null, $"{reason} The outcome is UNKNOWN: SAP B1 may have created the document. Releasing again first looks for it in SAP B1 and only posts if there is none.", batch.Count, OutcomeUnknown: true));
        }
    }

    /// <summary>A plain sentence for a connection-level failure.</summary>
    internal static string Describe(Exception ex)
    {
        var root = ex;
        while (root.InnerException is not null) root = root.InnerException;
        var detail = root.Message == ex.Message ? ex.Message : $"{ex.Message} ({root.Message})";
        return ex is TaskCanceledException or TimeoutException
            ? "The connection to SAP B1 timed out while sending the document."
            : $"The connection to SAP B1 failed while sending the document: {detail}";
    }
}
