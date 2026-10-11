using CobaltMrp.Domain;

namespace CobaltMrp.Data.ServiceLayer;

/// <summary>Outcome of pushing one planned order to B1.</summary>
public sealed record WritebackResult(Guid PlannedOrderId, bool Success, int? DocEntry, string? Error);

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

    /// <summary>Push each firmed order; unfirmed orders and already-pushed orders are skipped.
    /// On success <see cref="PlannedOrder.B1DocEntry"/> is set on the order.</summary>
    public async Task<WritebackSummary> PushAsync(IEnumerable<PlannedOrder> orders, CancellationToken ct = default)
    {
        var summary = new WritebackSummary();
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
        return summary;
    }
}
