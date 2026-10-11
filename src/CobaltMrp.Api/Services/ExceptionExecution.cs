using System.Text.Json;
using CobaltMrp.Api.Settings;
using CobaltMrp.Data.ServiceLayer;
using CobaltMrp.Domain;

namespace CobaltMrp.Api.Services;

public static class ExecuteActions
{
    public const string ReleaseOrder = "ReleaseOrder";
    public const string MoveDueDate = "MoveDueDate";
    public const string Close = "Close";
}

/// <summary>An exception as the UI sees it, including what Execute can do for it right now.</summary>
public sealed record ExceptionDto(
    Guid Id, ExceptionType Type, string ItemCode, string? WarehouseCode, string Message, DateOnly? RelatedDate,
    string? SourceDocKey, int? SourceLine, Guid? PlannedOrderId, DateOnly? CurrentDate, DateOnly? ProposedDate,
    decimal Quantity, IReadOnlyList<string> Actions, string? ActionStatus, string? ActionBy, DateTimeOffset? ActionAt, string? ActionNote)
{
    public static ExceptionDto From(ExceptionMessage e, MrpRun run) => new(
        e.ExceptionId, e.Type, e.ItemCode, e.WarehouseCode, e.Message, e.RelatedDate, e.SourceDocKey, e.SourceLine,
        e.PlannedOrderId, e.CurrentDate, e.ProposedDate, e.Quantity, ExceptionExecutor.AvailableActions(e, run),
        e.ActionStatus, e.ActionBy, e.ActionAt, e.ActionNote);
}

public sealed record ExecuteRequest(string? Action, DateOnly? NewDate, string? Note, bool Confirm);
public sealed record PreviewState(DateOnly? DueDate, string? Status);
public sealed record PreviewTarget(string Kind, int? DocEntry, int? Line);
public sealed record ExecutionPreview(
    string Action, bool CanExecute, string Title, string Summary, PreviewState? Current, PreviewState? Proposed,
    PreviewTarget Target, List<string> Warnings, List<string> Blockers);
public sealed record ExecuteResult(bool Ok, string Message, ExceptionDto Exception);

/// <summary>Raised with the HTTP status the endpoint should answer with.</summary>
public sealed class ExecuteException(int status, string message) : Exception(message) { public int Status { get; } = status; }

/// <summary>
/// Executes an exception: either creates the related planned order in SAP B1, or changes an existing purchase or
/// production order (move its due date, or close it) through the Service Layer. Every action is previewed first
/// (reading the live document), refuses to act on a document that changed since the plan, and is recorded on the
/// exception (who, when, note).
/// </summary>
public sealed class ExceptionExecutor
{
    private enum Kind { PurchaseOrder, ProductionOrder }
    private sealed record DocRef(Kind Kind, int DocEntry);

    private readonly SettingsService _settings;
    private readonly ReleaseService _release;
    private readonly ILogger<ExceptionExecutor> _log;
    public ExceptionExecutor(SettingsService settings, ReleaseService release, ILogger<ExceptionExecutor> log) { _settings = settings; _release = release; _log = log; }

    // ---- What can be done -------------------------------------------------------------------------------------

    private static DocRef? Parse(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var parts = key.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var entry)) return null;
        return parts[0].ToUpperInvariant() switch
        {
            "OPOR" => new DocRef(Kind.PurchaseOrder, entry),
            "OWOR" => new DocRef(Kind.ProductionOrder, entry),
            _ => null,
        };
    }

    public static IReadOnlyList<string> AvailableActions(ExceptionMessage e, MrpRun run)
    {
        if (e.ActionStatus == "Done") return Array.Empty<string>();
        var doc = Parse(e.SourceDocKey);
        switch (e.Type)
        {
            case ExceptionType.ReleaseInPast:
                var order = run.PlannedOrders.FirstOrDefault(o => o.Id == e.PlannedOrderId);
                return order is { Firmed: false } && string.IsNullOrEmpty(order.B1DocEntry)
                    ? new[] { ExecuteActions.ReleaseOrder } : Array.Empty<string>();
            case ExceptionType.Expedite:
                return doc is not null && e.ProposedDate is not null ? new[] { ExecuteActions.MoveDueDate } : Array.Empty<string>();
            case ExceptionType.Defer:
                return doc is not null ? new[] { ExecuteActions.MoveDueDate, ExecuteActions.Close } : Array.Empty<string>();
            case ExceptionType.Cancel:
                return doc is not null ? new[] { ExecuteActions.Close } : Array.Empty<string>();
            default:
                return Array.Empty<string>();      // past due, capacity, not planned: no automatic action
        }
    }

    // ---- Preview and execute ----------------------------------------------------------------------------------

    private sealed class Evaluation
    {
        public string Title = "", Summary = "";
        public PreviewState? Current, Proposed;
        public PreviewTarget Target = new("Unknown", null, null);
        public List<string> Warnings = new(), Blockers = new();
        public bool Conflict;          // the document no longer matches what the plan saw
        public DateOnly? LiveDate;
        public DocRef? Doc;
        public PlannedOrder? Order;
    }

    public async Task<ExecutionPreview> PreviewAsync(MrpRun run, ExceptionMessage e, ExecuteRequest req, CancellationToken ct)
    {
        var action = Validate(run, e, req, requireConfirm: false);
        var ev = await EvaluateAsync(run, e, action, req.NewDate, ct);
        return new ExecutionPreview(action, ev.Blockers.Count == 0, ev.Title, ev.Summary, ev.Current, ev.Proposed, ev.Target, ev.Warnings, ev.Blockers);
    }

    private static string DocName(PlannedOrderType t) => t switch
    {
        PlannedOrderType.PurchaseOrder => "purchase order",
        PlannedOrderType.PurchaseRequest => "purchase request",
        PlannedOrderType.TransferRequest => "transfer request",
        _ => "production order",
    };

    public async Task<ExecuteResult> ExecuteAsync(IRunStore store, MrpRun run, ExceptionMessage e, ExecuteRequest req, string user, ISapPushLog pushLog, CancellationToken ct)
    {
        var push = new PushContext(pushLog, user, run.Id, e.ExceptionId);
        var action = Validate(run, e, req, requireConfirm: true);
        var ev = await EvaluateAsync(run, e, action, req.NewDate, ct);
        if (ev.Conflict) throw new ExecuteException(409, ev.Blockers.FirstOrDefault() ?? "The document changed in SAP B1 since the plan. Re-run MRP.");
        if (ev.Blockers.Count > 0) throw new ExecuteException(400, string.Join(" ", ev.Blockers));

        string message;
        switch (action)
        {
            case ExecuteActions.ReleaseOrder:
                var outcome = await _release.ReleaseAsync(store, new[] { ev.Order! }, ct, push);
                if (outcome.Failures.Count > 0) throw new ExecuteException(502, outcome.Failures[0].Error);
                if (outcome.Pushed != 1) throw new ExecuteException(502, "The order was not created in SAP B1.");
                message = $"Created {DocName(ev.Order!.Type)} in SAP B1 (document {ev.Order.B1DocEntry}).";
                break;
            case ExecuteActions.MoveDueDate:
                message = await LoggedAsync(push, e, ev.Doc!, SapPushActions.MoveDueDate, $"Move due date to {req.NewDate:yyyy-MM-dd}", () => WithSessionAsync(async client =>
                {
                    var doc = ev.Doc!; var date = req.NewDate!.Value;
                    if (doc.Kind == Kind.PurchaseOrder)
                        await client.PatchEntityAsync($"PurchaseOrders({doc.DocEntry})",
                            new { DocumentLines = new[] { new { LineNum = e.SourceLine ?? 0, ShipDate = date.ToString("yyyy-MM-dd") } } }, ct);
                    else
                        await client.PatchEntityAsync($"ProductionOrders({doc.DocEntry})", new { DueDate = date.ToString("yyyy-MM-dd") }, ct);

                    // Read it back: never report success on the strength of a 204 alone.
                    var after = await Read(client, doc, e.SourceLine, ct);
                    if (after.Date != date)
                        throw new ExecuteException(502, $"SAP B1 accepted the change but the document now shows {after.Date:yyyy-MM-dd}, not {date:yyyy-MM-dd}.");
                    return doc.Kind == Kind.PurchaseOrder
                        ? $"Purchase order {doc.DocEntry} line {e.SourceLine ?? 0} moved to {date:yyyy-MM-dd}."
                        : $"Production order {doc.DocEntry} moved to {date:yyyy-MM-dd}.";
                }, ct));
                break;
            case ExecuteActions.Close:
                message = await LoggedAsync(push, e, ev.Doc!, SapPushActions.CloseDocument, ev.Doc!.Kind == Kind.PurchaseOrder ? "Close purchase order" : "Cancel production order", () => WithSessionAsync(async client =>
                {
                    var doc = ev.Doc!;
                    if (doc.Kind == Kind.PurchaseOrder)
                    {
                        await client.PostActionAsync($"PurchaseOrders({doc.DocEntry})/Close", ct);
                        return $"Purchase order {doc.DocEntry} closed.";
                    }
                    await client.PatchEntityAsync($"ProductionOrders({doc.DocEntry})", new { ProductionOrderStatus = "boposCancelled" }, ct);
                    return $"Production order {doc.DocEntry} cancelled.";
                }, ct));
                break;
            default:
                throw new ExecuteException(400, $"Unknown action '{action}'.");
        }

        e.ActionStatus = "Done";
        e.ActionBy = user;
        e.ActionAt = DateTimeOffset.UtcNow;
        e.ActionNote = string.IsNullOrWhiteSpace(req.Note) ? message : $"{req.Note!.Trim()} | {message}";
        if (e.ActionNote.Length > 500) e.ActionNote = e.ActionNote[..500];
        await store.SaveChangesAsync(ct);
        return new ExecuteResult(true, message, ExceptionDto.From(e, run));
    }

    private static string Validate(MrpRun run, ExceptionMessage e, ExecuteRequest req, bool requireConfirm)
    {
        if (e.ActionStatus == "Done") throw new ExecuteException(409, "This exception has already been executed.");
        var available = AvailableActions(e, run);
        if (available.Count == 0) throw new ExecuteException(400, "There is no automatic action for this exception.");
        var action = available.FirstOrDefault(a => string.Equals(a, req.Action, StringComparison.OrdinalIgnoreCase));
        if (action is null) throw new ExecuteException(400, $"Action '{req.Action}' is not available for this exception (available: {string.Join(", ", available)}).");
        if (requireConfirm && !req.Confirm) throw new ExecuteException(400, "Confirm that you understand this changes SAP Business One.");
        return action;
    }

    // ---- Evaluation (reads SAP B1, changes nothing) ----------------------------------------------------------

    private async Task<Evaluation> EvaluateAsync(MrpRun run, ExceptionMessage e, string action, DateOnly? newDate, CancellationToken ct)
    {
        var sl = _settings.Get().ServiceLayer;
        var ev = new Evaluation();
        if (!(sl.Enabled && sl.IsConfigured))
            ev.Blockers.Add("Service Layer write-back is off. Enable it in Settings > Service Layer.");

        if (action == ExecuteActions.ReleaseOrder)
        {
            var o = run.PlannedOrders.First(x => x.Id == e.PlannedOrderId);
            ev.Order = o;
            var name = DocName(o.Type);
            ev.Target = new PreviewTarget("PlannedOrder", null, null);
            ev.Title = $"Create {name} for {o.ItemCode}";
            var vendor = VendorResolver.Resolve(o, sl);
            var detail = o.Type switch
            {
                PlannedOrderType.PurchaseOrder => $", supplier {vendor.Code ?? "(none)"}{(vendor.Source is null ? "" : $" ({vendor.Source})")}.",
                PlannedOrderType.PurchaseRequest => vendor.Code is null ? "." : $", suggested supplier {vendor.Code}.",
                PlannedOrderType.TransferRequest => $", from warehouse {o.FromWarehouse} to {o.WarehouseCode}.",
                _ => ".",
            };
            ev.Summary = $"Create a {name} in SAP B1: {o.ItemCode}, quantity {o.Quantity:0.##}, due {o.DueDate:yyyy-MM-dd}{detail}";
            ev.Proposed = new PreviewState(o.DueDate, "New");
            if (o.Type == PlannedOrderType.PurchaseOrder && vendor.Code is null) ev.Blockers.Add("This purchase order has no supplier. Set one in Master data first.");
            if (o.Type == PlannedOrderType.TransferRequest && string.IsNullOrWhiteSpace(o.FromWarehouse)) ev.Blockers.Add("This transfer has no sending warehouse.");
            if (o.ReleaseDate < DateOnly.FromDateTime(DateTime.Today)) ev.Warnings.Add($"Its release date ({o.ReleaseDate:yyyy-MM-dd}) is already in the past: the supply will arrive late.");
            return ev;
        }

        var doc = Parse(e.SourceDocKey)!;
        ev.Doc = doc;
        var docName = doc.Kind == Kind.PurchaseOrder ? "purchase order" : "production order";
        ev.Target = new PreviewTarget(doc.Kind.ToString(), doc.DocEntry, doc.Kind == Kind.PurchaseOrder ? e.SourceLine ?? 0 : null);
        var where = doc.Kind == Kind.PurchaseOrder ? $"{docName} {doc.DocEntry}, line {e.SourceLine ?? 0}" : $"{docName} {doc.DocEntry}";

        if (action == ExecuteActions.MoveDueDate)
        {
            ev.Title = $"Move {where}";
            if (newDate is null) ev.Blockers.Add("Choose the new due date.");
            else if (newDate < DateOnly.FromDateTime(DateTime.Today)) ev.Blockers.Add("The new date cannot be in the past.");
            ev.Proposed = new PreviewState(newDate, null);
        }
        else
        {
            ev.Title = $"Close {where}";
            ev.Summary = doc.Kind == Kind.PurchaseOrder ? $"Close {docName} {doc.DocEntry} in SAP B1." : $"Cancel {docName} {doc.DocEntry} in SAP B1.";
            ev.Proposed = new PreviewState(null, doc.Kind == Kind.PurchaseOrder ? "Closed" : "Cancelled");
        }

        // Look at the live document. Only possible (and only needed) when write-back is on.
        DateOnly? live = null; string? status = null; int openLines = 0; bool partial = false;
        if (sl.Enabled && sl.IsConfigured)
        {
            try
            {
                var snap = await WithSessionAsync(async client => await Read(client, doc, e.SourceLine, ct), ct);
                live = snap.Date; status = snap.Status; openLines = snap.OpenLines; partial = snap.PartiallyReceived;
                if (!snap.Found) ev.Blockers.Add($"The {where} was not found in SAP B1.");
                else if (!snap.IsOpen) ev.Blockers.Add($"The {docName} is not open in SAP B1 (status {snap.Status}).");
            }
            catch (ExecuteException) { throw; }
            catch (Exception ex) { ev.Blockers.Add($"Could not read the {docName} from SAP B1: {ex.Message}"); }
        }
        ev.LiveDate = live;
        ev.Current = new PreviewState(live ?? e.CurrentDate, Friendly(status));

        if (live is not null && e.CurrentDate is not null && live != e.CurrentDate)
        {
            ev.Conflict = true;
            ev.Blockers.Add($"The {docName} changed in SAP B1 since the plan: it is now due {live:yyyy-MM-dd}, the plan saw {e.CurrentDate:yyyy-MM-dd}. Re-run MRP.");
        }
        if (partial) ev.Warnings.Add("The order has already been received in part.");
        if (action == ExecuteActions.MoveDueDate)
        {
            var from = live ?? e.CurrentDate;
            if (newDate is not null && from == newDate) ev.Blockers.Add("The order is already due on that date.");
            ev.Summary = $"Due date {(from is null ? "?" : from.Value.ToString("yyyy-MM-dd"))} -> {(newDate is null ? "?" : newDate.Value.ToString("yyyy-MM-dd"))}";
        }
        else if (action == ExecuteActions.Close)
        {
            if (doc.Kind == Kind.PurchaseOrder && openLines > 1)
                ev.Blockers.Add($"This purchase order has {openLines} open lines; closing it would close all of them. Close the line in SAP B1 instead.");
            if (doc.Kind == Kind.ProductionOrder && string.Equals(status, "boposReleased", StringComparison.OrdinalIgnoreCase))
                ev.Blockers.Add("A released production order may have issues and receipts pending: close it in SAP B1.");
            ev.Warnings.Add("This cannot be undone from Cobalt MRP.");
        }
        return ev;
    }

    /// <summary>SAP B1 status codes in words a planner knows.</summary>
    private static string? Friendly(string? status) => status switch
    {
        "bost_Open" => "Open",
        "bost_Close" => "Closed",
        "boposPlanned" => "Planned",
        "boposReleased" => "Released",
        "boposClosed" => "Closed",
        "boposCancelled" => "Cancelled",
        _ => status,
    };

    private sealed record Snapshot(bool Found, bool IsOpen, string? Status, DateOnly? Date, int OpenLines, bool PartiallyReceived);

    private static async Task<Snapshot> Read(ServiceLayerClient client, DocRef doc, int? line, CancellationToken ct)
    {
        JsonElement j;
        try { j = await client.GetEntityAsync($"{(doc.Kind == Kind.PurchaseOrder ? "PurchaseOrders" : "ProductionOrders")}({doc.DocEntry})", ct); }
        catch (ServiceLayerException ex) when (ex.Message.Contains("404") || ex.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase))
        { return new Snapshot(false, false, null, null, 0, false); }

        if (doc.Kind == Kind.ProductionOrder)
        {
            var st = Str(j, "ProductionOrderStatus");
            var open = st is "boposPlanned" or "boposReleased";
            return new Snapshot(true, open, st, Date(j, "DueDate"), 0, false);
        }

        var docStatus = Str(j, "DocumentStatus");
        var lines = j.TryGetProperty("DocumentLines", out var dl) && dl.ValueKind == JsonValueKind.Array ? dl.EnumerateArray().ToList() : new List<JsonElement>();
        var target = lines.FirstOrDefault(l => l.TryGetProperty("LineNum", out var n) && n.GetInt32() == (line ?? 0));
        var found = target.ValueKind == JsonValueKind.Object;
        var lineOpen = found && Str(target, "LineStatus") is "bost_Open" or null;
        var openLines = lines.Count(l => Str(l, "LineStatus") is "bost_Open");
        var partial = found && target.TryGetProperty("Quantity", out var q) && target.TryGetProperty("RemainingOpenQuantity", out var r)
                      && q.ValueKind == JsonValueKind.Number && r.ValueKind == JsonValueKind.Number && r.GetDecimal() < q.GetDecimal();
        return new Snapshot(found, docStatus is "bost_Open" && lineOpen, docStatus, found ? Date(target, "ShipDate") : null, openLines, partial);
    }

    /// <summary>Runs one change in SAP B1 and logs it either way (a failure is logged, then rethrown unchanged).</summary>
    private async Task<string> LoggedAsync(PushContext push, ExceptionMessage e, DocRef doc, string action, string what, Func<Task<string>> work)
    {
        var entry = new SapPushLogEntry
        {
            Action = action, ItemCode = e.ItemCode, WarehouseCode = e.WarehouseCode, Quantity = e.Quantity,
            Target = $"{(doc.Kind == Kind.PurchaseOrder ? "OPOR" : "OWOR")}:{doc.DocEntry}",
            Summary = doc.Kind == Kind.PurchaseOrder && e.SourceLine is not null ? $"{what} (line {e.SourceLine})" : what,
        };
        try
        {
            var message = await work();
            entry.Success = true; entry.Summary = message;
            await SapPushRecorder.RecordAsync(push, _log, new[] { entry });
            return message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            entry.Success = false; entry.Error = ex.Message;
            await SapPushRecorder.RecordAsync(push, _log, new[] { entry });
            throw;
        }
    }

    private static string? Str(JsonElement j, string name) =>
        j.ValueKind == JsonValueKind.Object && j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateOnly? Date(JsonElement j, string name)
    {
        var s = Str(j, name);
        return s is { Length: >= 10 } && DateOnly.TryParse(s[..10], out var d) ? d : null;
    }

    private async Task<T> WithSessionAsync<T>(Func<ServiceLayerClient, Task<T>> work, CancellationToken ct)
    {
        var sl = _settings.Get().ServiceLayer;
        using var client = new ServiceLayerClient(ServiceLayerSession.Options(sl));
        await client.LoginAsync(ct);
        try { return await work(client); }
        catch (ServiceLayerException ex) { throw new ExecuteException(502, ex.Message); }
        finally { try { await client.LogoutAsync(ct); } catch { /* the session expires on its own */ } }
    }
}
