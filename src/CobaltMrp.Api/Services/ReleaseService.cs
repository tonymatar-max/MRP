using CobaltMrp.Api.Settings;
using CobaltMrp.Data.ServiceLayer;
using CobaltMrp.Domain;

namespace CobaltMrp.Api.Services;

public sealed record ReleaseFailure(Guid? PlannedOrderId, string Error);
/// <param name="Documents">SAP B1 documents created (several planned orders can share one).</param>
/// <param name="AlreadyInSap">Selected orders that already had their SAP B1 document from an earlier release: skipped, not failures.</param>
/// <param name="Adopted">Orders linked to a document that was already in SAP B1 (an earlier attempt whose answer was lost) instead of posting a duplicate.</param>
public sealed record ReleaseOutcome(bool WritebackEnabled, int Pushed, List<ReleaseFailure> Failures, int Documents = 0, int AlreadyInSap = 0, int Adopted = 0);

/// <summary>
/// Firms planned orders and, when Service Layer write-back is enabled, creates them in SAP B1 one by one.
/// Shared by "Release" on Planned Orders and by "Execute" on a release-in-past exception, so both behave the same.
/// </summary>
public sealed class ReleaseService
{
    private readonly SettingsService _settings;
    private readonly ILogger<ReleaseService> _log;
    public ReleaseService(SettingsService settings, ILogger<ReleaseService> log) { _settings = settings; _log = log; }

    public async Task<ReleaseOutcome> ReleaseAsync(IRunStore store, IReadOnlyList<PlannedOrder> selected, CancellationToken ct, PushContext? context = null, DocumentGrouping grouping = DocumentGrouping.None)
    {
        foreach (var o in selected) o.Firmed = true;
        await store.SaveChangesAsync(ct);

        var sl = _settings.Get().ServiceLayer;
        var enabled = sl.Enabled && sl.IsConfigured;
        var failures = new List<ReleaseFailure>();
        var pushed = 0;
        if (!enabled) return new ReleaseOutcome(false, 0, failures);

        using var client = new ServiceLayerClient(ServiceLayerSession.Options(sl));
        var summary = new WritebackSummary();
        try
        {
            await client.LoginAsync(ct);
            await new PlannedOrderWriteback(client, o => VendorResolver.Resolve(o, sl).Code).PushAsync(selected, ct, summary, grouping);
            // Log first: these documents now exist in SAP B1 whatever happens to the saves below.
            await SapPushRecorder.RecordAsync(context, _log, summary.Results.Where(r => !WasSkipped(r)).Select(r => Entry(selected.FirstOrDefault(x => x.Id == r.PlannedOrderId), r)).ToList());
            pushed = summary.Succeeded.Count();
            // Orders that already have their SAP B1 document are a no-op, not a failure.
            failures.AddRange(summary.Failed.Where(f => !f.AlreadyPushed).Select(f => new ReleaseFailure(f.PlannedOrderId, f.Error ?? "Unknown error")));
            // Keep SAP B1's complete message: the UI shows it once, the Event Log keeps it for good.
            foreach (var f in summary.Failed.Where(f => !f.AlreadyPushed))
            {
                var o = selected.FirstOrDefault(x => x.Id == f.PlannedOrderId);
                _log.LogWarning("Release to SAP B1 failed for {Item} ({Type}, qty {Qty}, planned order {Id}): {Error}",
                    o?.ItemCode, o?.Type, o?.Quantity, f.PlannedOrderId, f.Error);
            }
            await store.SaveChangesAsync(ct);   // persists B1DocEntry for the ones that went through
            await client.LogoutAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled part-way: documents already created in SAP B1 must still be logged and remembered
            // (B1DocEntry), otherwise a retry would create them a second time.
            await SapPushRecorder.RecordAsync(context, _log, summary.Results.Where(r => !WasSkipped(r))
                .Select(r => Entry(selected.FirstOrDefault(x => x.Id == r.PlannedOrderId), r)).ToList());
            try { await store.SaveChangesAsync(CancellationToken.None); }
            catch (Exception ex) { _log.LogError(ex, "Could not save B1DocEntry after a cancelled release"); }
            throw;
        }
        catch (Exception ex)
        {
            // Anything else (login failed, network down, a timeout): answer with the normal result plus a failure, never an HTTP 500.
            _log.LogError(ex, "Service Layer write-back failed");
            var text = ServiceLayerDiagnostics.Explain(ex, sl.BaseUrl, sl.AllowInvalidCertificates);
            failures.Add(new ReleaseFailure(null, $"Service Layer: {text}"));
            await SapPushRecorder.RecordAsync(context, _log, new List<SapPushLogEntry>
            {
                new() { Action = SapPushActions.Session, Success = false, Summary = $"Service Layer write-back stopped while releasing {selected.Count} planned order(s)", Error = text },
            });
            // Whatever was already created and logged must still be remembered.
            try { await store.SaveChangesAsync(CancellationToken.None); }
            catch (Exception saveEx) { _log.LogError(saveEx, "Could not save B1DocEntry after a failed release"); }
        }
        var created = summary.Succeeded.Where(r => !r.Adopted);
        var documents = created.Select(r => (selected.FirstOrDefault(x => x.Id == r.PlannedOrderId)?.Type, r.DocEntry)).Distinct().Count();
        return new ReleaseOutcome(true, summary.Succeeded.Count(), failures, documents, summary.Results.Count(r => r.AlreadyPushed), summary.Succeeded.Count(r => r.Adopted));
    }

    /// <summary>Orders the write-back declined before calling SAP B1 (not firmed, already pushed): nothing was sent, so nothing is logged.</summary>
    private static bool WasSkipped(WritebackResult r) =>
        !r.Success && (r.AlreadyPushed || (r.Error is not null && r.Error.StartsWith("Order is not firmed", StringComparison.Ordinal)));

    private static SapPushLogEntry Entry(PlannedOrder? o, WritebackResult r) => new()
    {
        Action = o is null ? SapPushActions.Session : SapPushActions.ForCreate(o.Type),
        Success = r.Success,
        PlannedOrderId = r.PlannedOrderId,
        ItemCode = o?.ItemCode, WarehouseCode = o?.WarehouseCode, Quantity = o?.Quantity,
        Target = r.Success && o is not null ? $"{SapPushDescribe.DocPrefix(o.Type)}:{r.DocEntry}" : null,
        Summary = o is null ? "Planned order not found" : SapPushDescribe.Summary(o) + (r.DocLines > 1 ? $"; one of {r.DocLines} lines on the same document" : "")
            + (r.Adopted ? "; the document already existed in SAP B1 (an earlier attempt whose answer was lost), checked by its planned-order tag and adopted, nothing posted" : "")
            + (r.OutcomeUnknown ? "; outcome unknown after a connection error, SAP B1 could not be checked" : ""),
        Error = r.Success ? null : r.Error ?? "Unknown error",
    };
}

public static class SapPushDescribe
{
    /// <summary>The SAP B1 table prefix used in document keys elsewhere in the app (OPOR = purchase order).</summary>
    public static string DocPrefix(PlannedOrderType t) => t switch
    {
        PlannedOrderType.PurchaseOrder => "OPOR",
        PlannedOrderType.PurchaseRequest => "OPRQ",
        PlannedOrderType.TransferRequest => "OWTQ",
        _ => "OWOR",
    };

    public static string Summary(PlannedOrder o)
    {
        var what = o.Type switch
        {
            PlannedOrderType.PurchaseOrder => "Purchase order",
            PlannedOrderType.PurchaseRequest => "Purchase request",
            PlannedOrderType.TransferRequest => "Transfer request",
            _ => "Production order",
        };
        var where = o.Type == PlannedOrderType.TransferRequest ? $"{o.FromWarehouse} -> {o.WarehouseCode}" : o.WarehouseCode;
        return $"{what}: {o.ItemCode} x {o.Quantity:0.###}, due {o.DueDate:yyyy-MM-dd}, warehouse {where}";
    }
}

public static class ServiceLayerSession
{
    public static ServiceLayerOptions Options(ServiceLayerSettings sl) => new()
    {
        BaseUrl = (sl.BaseUrl ?? "").Trim(), CompanyDb = (sl.CompanyDb ?? "").Trim(), UserName = (sl.UserName ?? "").Trim(),
        Password = sl.Password, AllowInvalidCertificates = sl.AllowInvalidCertificates,
    };
}
