using System.Globalization;
using System.Text.Json;
using CobaltMrp.Api.Settings;
using CobaltMrp.Data.ServiceLayer;
using CobaltMrp.Domain;

namespace CobaltMrp.Api.Services;

/// <summary>A Cobalt override that has a matching field on the SAP B1 item master.</summary>
public sealed record PushField(string Field, string SapProperty, string Label, bool Integer = false);

public static class ItemPushFields
{
    /// <summary>
    /// Cobalt override -> Service Layer Items property. Verified against SBODemoHO: Mainsupplier = OITM.CardCode,
    /// MinInventory = OITM.MinLevel, MaxInventory = OITM.MaxLevel, DesiredInventory = OITM.ReorderQty.
    /// </summary>
    public static readonly IReadOnlyList<PushField> All = new PushField[]
    {
        new("supplier", "Mainsupplier", "Default supplier"),
        new("leadTimeDays", "LeadTime", "Lead time (days)", Integer: true),
        new("minOrderQty", "MinOrderQuantity", "Minimum order quantity"),
        new("orderMultiple", "OrderMultiple", "Order multiple"),
        new("safetyStock", "MinInventory", "Safety stock (minimum inventory)"),
        new("maxStockLevel", "MaxInventory", "Maximum inventory"),
        new("fixedLotSize", "DesiredInventory", "Fixed lot size (required inventory)"),
    };

    /// <summary>Overridable in Cobalt but with no field in SAP B1 to hold them.</summary>
    public static readonly IReadOnlyList<string> CobaltOnly = new[] { "reorderPoint", "planningMethod", "lotSizing", "periodOfSupplyDays" };

    public static string Select => "ItemCode,ItemName," + string.Join(",", All.Select(f => f.SapProperty));
}

/// <summary>Status is "change", "same" (SAP B1 already has it) or "blocked" (see Message).</summary>
public sealed record PushPreviewField(string Field, string Label, string Cobalt, string? Sap, string Status, string? Message);
public sealed record PushPreviewItem(string ItemCode, string? ItemName, bool Found, List<PushPreviewField> Fields, List<string> CobaltOnly, string? Error);
public sealed record PushPreview(bool Ready, string? NotReadyReason, List<PushPreviewItem> Items);

public sealed record PushPreviewRequest(List<string>? ItemCodes);
public sealed record PushRequestField(string Field, string? ExpectedSap, string Value);
public sealed record PushRequestItem(string ItemCode, List<PushRequestField>? Fields);
public sealed record PushExecuteRequest(List<PushRequestItem>? Items, bool Confirm, bool ClearOverrides);
public sealed record PushItemResult(string ItemCode, bool Success, string Message, List<string> Pushed);
public sealed record PushExecuteResult(int Pushed, int Failed, List<PushItemResult> Items);

/// <summary>Raised with the HTTP status the endpoint should answer with.</summary>
public sealed class PushException(int status, string message) : Exception(message) { public int Status { get; } = status; }

/// <summary>
/// Pushes the planner's master-data overrides to the SAP B1 item master through the Service Layer. Nothing happens by itself:
/// a preview reads the live items, and a push only changes the fields the planner ticked, only if SAP B1 still holds the value the
/// preview showed, reads each item back, and records every item in the SAP push log.
/// </summary>
public sealed class ItemPushService
{
    private readonly SettingsService _settings;
    private readonly ILogger<ItemPushService> _log;
    private readonly Func<ServiceLayerSettings, ServiceLayerClient> _clientFactory;

    public ItemPushService(SettingsService settings, ILogger<ItemPushService> log, Func<ServiceLayerSettings, ServiceLayerClient>? clientFactory = null)
    {
        _settings = settings; _log = log;
        _clientFactory = clientFactory ?? (sl => new ServiceLayerClient(ServiceLayerSession.Options(sl)));
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private (bool Ready, string? Reason) Readiness()
    {
        var sl = _settings.Get().ServiceLayer;
        return sl.Enabled && sl.IsConfigured ? (true, null) : (false, "Service Layer write-back is off. Enable it in Settings > Service Layer.");
    }

    // ---- value helpers -----------------------------------------------------------------------------------------

    internal static string Format(PushField f, decimal v) => f.Integer ? ((int)v).ToString(Inv) : v.ToString("0.######", Inv);

    /// <summary>The override of one field as text, or null when it is not overridden.</summary>
    internal static string? OverrideValue(ItemOverride o, string field) => field switch
    {
        "supplier" => string.IsNullOrWhiteSpace(o.Supplier) ? null : o.Supplier.Trim(),
        "leadTimeDays" => o.LeadTimeDays?.ToString(Inv),
        "minOrderQty" => o.MinOrderQty?.ToString("0.######", Inv),
        "orderMultiple" => o.OrderMultiple?.ToString("0.######", Inv),
        "safetyStock" => o.SafetyStock?.ToString("0.######", Inv),
        "maxStockLevel" => o.MaxStockLevel?.ToString("0.######", Inv),
        "fixedLotSize" => o.FixedLotSize?.ToString("0.######", Inv),
        _ => null,
    };

    private static string? LiveValue(JsonElement item, PushField f)
    {
        if (!item.TryGetProperty(f.SapProperty, out var v) || v.ValueKind == JsonValueKind.Null) return f.Field == "supplier" ? null : "0";
        if (v.ValueKind == JsonValueKind.String) return string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim();
        if (v.ValueKind == JsonValueKind.Number) return Format(f, v.GetDecimal());
        return v.ToString();
    }

    private static bool Same(PushField f, string? a, string? b)
    {
        if (f.Field == "supplier") return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        return decimal.TryParse(a ?? "0", NumberStyles.Number, Inv, out var x) && decimal.TryParse(b ?? "0", NumberStyles.Number, Inv, out var y) && x == y;
    }

    private static string? Validate(PushField f, string value)
    {
        if (f.Field == "supplier") return string.IsNullOrWhiteSpace(value) ? "The supplier code is empty." : null;
        if (!decimal.TryParse(value, NumberStyles.Number, Inv, out var n)) return $"'{value}' is not a number.";
        if (n < 0) return "The value cannot be negative.";
        if (f.Integer && (n != decimal.Truncate(n) || n > 3650)) return "Lead time must be a whole number of days from 0 to 3650.";
        return null;
    }

    private static string Esc(string code) => Uri.EscapeDataString(code.Replace("'", "''"));

    private async Task<JsonElement?> ReadItemAsync(ServiceLayerClient client, string code, CancellationToken ct)
    {
        try { return await client.GetEntityAsync($"Items('{Esc(code)}')?$select={ItemPushFields.Select}", ct); }
        catch (ServiceLayerException ex) when (ex.Message.Contains("404") || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                                               || ex.Message.Contains("No matching records", StringComparison.OrdinalIgnoreCase)) { return null; }
    }

    private async Task<string?> SupplierProblemAsync(ServiceLayerClient client, string code, Dictionary<string, string?> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(code, out var known)) return known;
        string? problem = null;
        try
        {
            var bp = await client.GetEntityAsync($"BusinessPartners('{Esc(code)}')?$select=CardCode,CardType,Frozen,Valid", ct);
            var type = bp.TryGetProperty("CardType", out var t) ? t.GetString() : null;
            if (type is not ("cSupplier" or "cLid")) problem = $"{code} is not a supplier in SAP B1 (it is a {type?.Replace("c", "").ToLowerInvariant() ?? "business partner of another type"}).";
            else if (bp.TryGetProperty("Frozen", out var fz) && fz.GetString() == "tYES") problem = $"Supplier {code} is frozen in SAP B1.";
        }
        catch (ServiceLayerException) { problem = $"Supplier {code} was not found in SAP B1."; }
        cache[code] = problem;
        return problem;
    }

    // ---- preview (reads SAP B1, changes nothing) ---------------------------------------------------------------

    public async Task<PushPreview> PreviewAsync(IReadOnlyDictionary<string, ItemOverride> overrides, IReadOnlyList<string> itemCodes, CancellationToken ct)
    {
        var (ready, reason) = Readiness();
        var items = new List<PushPreviewItem>();
        if (!ready)
        {
            foreach (var c in itemCodes) items.Add(Skeleton(c, overrides));
            return new PushPreview(false, reason, items);
        }

        var sl = _settings.Get().ServiceLayer;
        using var client = _clientFactory(sl);
        try
        {
            await client.LoginAsync(ct);
            var suppliers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in itemCodes)
            {
                var item = Skeleton(code, overrides);
                if (item.Fields.Count == 0) { items.Add(item); continue; }
                var live = await ReadItemAsync(client, code, ct);
                if (live is null) { items.Add(item with { Found = false, Error = $"Item {code} was not found in SAP B1.", Fields = new() }); continue; }
                var name = live.Value.TryGetProperty("ItemName", out var n) ? n.GetString() : null;
                var fields = new List<PushPreviewField>();
                foreach (var f in item.Fields)
                {
                    var f0 = ItemPushFields.All.First(x => x.Field == f.Field);
                    var sap = LiveValue(live.Value, f0);
                    var bad = Validate(f0, f.Cobalt);
                    if (bad is null && f0.Field == "supplier") bad = await SupplierProblemAsync(client, f.Cobalt, suppliers, ct);
                    fields.Add(bad is not null ? f with { Sap = sap, Status = "blocked", Message = bad }
                        : Same(f0, f.Cobalt, sap) ? f with { Sap = sap, Status = "same", Message = "SAP B1 already has this value." }
                        : f with { Sap = sap, Status = "change" });
                }
                items.Add(item with { ItemName = name, Fields = fields });
            }
        }
        catch (ServiceLayerException ex) { throw new PushException(502, ex.Message); }
        finally { try { await client.LogoutAsync(ct); } catch { /* the session expires on its own */ } }
        return new PushPreview(true, null, items);
    }

    private static PushPreviewItem Skeleton(string code, IReadOnlyDictionary<string, ItemOverride> overrides)
    {
        var fields = new List<PushPreviewField>();
        var cobaltOnly = new List<string>();
        if (overrides.TryGetValue(code, out var o))
        {
            foreach (var f in ItemPushFields.All)
                if (OverrideValue(o, f.Field) is { } v) fields.Add(new PushPreviewField(f.Field, f.Label, v, null, "change", null));
            cobaltOnly.AddRange(o.OverriddenFields().Where(x => ItemPushFields.CobaltOnly.Contains(x)));
        }
        return new PushPreviewItem(code, null, true, fields, cobaltOnly, null);
    }

    // ---- push -------------------------------------------------------------------------------------------------

    public async Task<PushExecuteResult> ExecuteAsync(PushExecuteRequest req, IMasterStore store, PushContext push, CancellationToken ct)
    {
        if (!req.Confirm) throw new PushException(400, "Confirm that you understand this changes SAP Business One.");
        if (req.Items is null || req.Items.Count == 0) throw new PushException(400, "Nothing was selected to push.");
        if (req.Items.Count > 200) throw new PushException(400, "At most 200 items can be pushed at once.");
        var (ready, reason) = Readiness();
        if (!ready) throw new PushException(400, reason!);

        var overrides = await store.GetItemOverridesAsync(ct);
        var sl = _settings.Get().ServiceLayer;
        var results = new List<PushItemResult>();
        using var client = _clientFactory(sl);
        try
        {
            await client.LoginAsync(ct);
            var suppliers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var req1 in req.Items)
                results.Add(await PushOneAsync(client, req1, req.ClearOverrides, overrides, store, push, suppliers, ct));
        }
        catch (ServiceLayerException ex) { throw new PushException(502, ex.Message); }
        finally { try { await client.LogoutAsync(ct); } catch { /* the session expires on its own */ } }
        return new PushExecuteResult(results.Count(r => r.Success), results.Count(r => !r.Success), results);
    }

    private async Task<PushItemResult> PushOneAsync(ServiceLayerClient client, PushRequestItem req, bool clear, IReadOnlyDictionary<string, ItemOverride> overrides,
        IMasterStore store, PushContext push, Dictionary<string, string?> suppliers, CancellationToken ct)
    {
        var code = req.ItemCode;
        var entry = new SapPushLogEntry { Action = SapPushActions.UpdateItem, ItemCode = code, Target = $"OITM:{code}", Summary = $"Item master {code}" };
        async Task<PushItemResult> FailAsync(string msg)
        {
            entry.Success = false; entry.Error = msg;
            await SapPushRecorder.RecordAsync(push, _log, new[] { entry });
            return new PushItemResult(code, false, msg, new());
        }

        if (req.Fields is null || req.Fields.Count == 0) return new PushItemResult(code, true, "Nothing to change.", new());
        try
        {
            var live = await ReadItemAsync(client, code, ct);
            if (live is null) return await FailAsync($"Item {code} was not found in SAP B1.");

            var body = new Dictionary<string, object?>();
            var changes = new List<string>();
            var pushed = new List<string>();
            foreach (var rf in req.Fields)
            {
                var f = ItemPushFields.All.FirstOrDefault(x => x.Field == rf.Field);
                if (f is null) return await FailAsync($"'{rf.Field}' cannot be pushed to SAP B1 (no matching field).");
                if (Validate(f, rf.Value) is { } bad) return await FailAsync($"{f.Label}: {bad}");
                var now = LiveValue(live.Value, f);
                if (rf.ExpectedSap is not null && !Same(f, now, rf.ExpectedSap))
                    return await FailAsync($"{f.Label} changed in SAP B1 since the preview (it is now {now ?? "empty"}, the preview showed {rf.ExpectedSap}). Preview again.");
                if (Same(f, now, rf.Value)) { pushed.Add(rf.Field); continue; }      // already there: nothing to write, the override can still be cleared
                if (f.Field == "supplier" && await SupplierProblemAsync(client, rf.Value, suppliers, ct) is { } sp) return await FailAsync(sp);
                body[f.SapProperty] = f.Field == "supplier" ? rf.Value.Trim()
                    : f.Integer ? int.Parse(rf.Value, Inv) : decimal.Parse(rf.Value, Inv);
                changes.Add($"{f.Label.ToLowerInvariant()} {now ?? "(none)"} -> {rf.Value}");
                pushed.Add(rf.Field);
            }

            if (body.Count > 0)
            {
                await client.PatchEntityAsync($"Items('{Esc(code)}')", body, ct);
                // Never trust a 204 alone: read the item back and compare every field we wrote.
                var after = await ReadItemAsync(client, code, ct);
                foreach (var rf in req.Fields)
                {
                    var f = ItemPushFields.All.First(x => x.Field == rf.Field);
                    if (after is null || !Same(f, LiveValue(after.Value, f), rf.Value))
                        return await FailAsync($"SAP B1 accepted the change but {f.Label} now shows {(after is null ? "?" : LiveValue(after.Value, f) ?? "empty")}, not {rf.Value}.");
                }
            }

            if (clear && pushed.Count > 0 && overrides.TryGetValue(code, out var ov))
            {
                var next = ov.Clone();
                foreach (var field in pushed) ClearField(next, field);
                next.UpdatedBy = push.User; next.UpdatedAt = DateTimeOffset.UtcNow;
                await store.SaveItemsAsync(new[] { next }, ct);
            }

            entry.Success = true;
            entry.Summary = body.Count == 0 ? $"Item master {code}: already up to date" : $"Item master {code}: {string.Join("; ", changes)}";
            await SapPushRecorder.RecordAsync(push, _log, new[] { entry });
            return new PushItemResult(code, true, body.Count == 0 ? "SAP B1 already had these values." : string.Join("; ", changes), pushed);
        }
        catch (OperationCanceledException) { throw; }
        catch (ServiceLayerException ex) { return await FailAsync(ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { return await FailAsync($"The connection to SAP B1 failed: {ex.Message}. Check the item in SAP B1 before pushing again."); }
    }

    private static void ClearField(ItemOverride o, string field)
    {
        switch (field)
        {
            case "supplier": o.Supplier = null; break;
            case "leadTimeDays": o.LeadTimeDays = null; break;
            case "minOrderQty": o.MinOrderQty = null; break;
            case "orderMultiple": o.OrderMultiple = null; break;
            case "safetyStock": o.SafetyStock = null; break;
            case "maxStockLevel": o.MaxStockLevel = null; break;
            case "fixedLotSize": o.FixedLotSize = null; break;
        }
    }
}
