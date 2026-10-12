using System.Text;
using CobaltMrp.Api.Services;
using CobaltMrp.Api.Settings;
using CobaltMrp.Domain;
using CobaltMrp.Engine;

namespace CobaltMrp.Api.Api;

public sealed record PlanValues(
    PlanningMethod PlanningMethod, LotSizingRule LotSizing, int? LeadTimeDays, int? GroupLeadTimeDays,
    decimal MinOrderQty, decimal OrderMultiple, decimal SafetyStock, decimal ReorderPoint, decimal MaxStockLevel,
    decimal FixedLotSize, int PeriodOfSupplyDays, string? Supplier, string? SupplierName);

public sealed record LeadTimeInfo(int Days, string Source, int? SupplierDays, int? ItemDays, int? GroupDays, int SupplierItemCount);

public sealed record MasterItemRow(
    string ItemCode, string? ItemName, string? GroupCode, string? GroupName, ProcurementType Procurement,
    PlanValues B1, ItemOverride? Override, PlanValues Effective, LeadTimeInfo LeadTime, IReadOnlyList<string> OverriddenFields);

public sealed record BulkRequest(string[]? ItemCodes, ItemOverride? Set, string[]? Clear);
public sealed record SupplierRow(string CardCode, string? CardName, int? LeadTimeDays, string Notes, int ItemCount, string? UpdatedBy, DateTimeOffset? UpdatedAt);
public sealed record SupplierEdit(int? LeadTimeDays, string? Notes);
/// <summary>One line of a supplier lead-time file. Values arrive as text so a bad cell is reported on its own line.</summary>
public sealed record SupplierImportRow(int Line, string? CardCode, string? LeadTimeDays, string? Notes);
public sealed record SupplierImportRequest(List<SupplierImportRow>? Rows);
/// <summary>Status is "change" (will be / was applied), "same" (nothing to change) or "error".</summary>
public sealed record SupplierImportResult(int Line, string CardCode, string Status, string Message, int? Before, int? After);

/// <summary>
/// Master data for planning: SAP B1's item and supplier data with the planner's Cobalt-side overrides on top.
/// Nothing here writes to SAP B1.
/// </summary>
public static class MasterDataEndpoints
{
    public static void MapMasterData(this RouteGroupBuilder api)
    {
        api.MapGet("/lookups/item-groups", async (string? source, ProviderFactory providers, CancellationToken ct) =>
        {
            var (m, error) = await LoadMaster(providers, source, ct);
            return m is null ? Results.BadRequest(new { message = error })
                : Results.Ok(m.Groups.Select(g => new { code = g.Code, name = g.Name, leadTimeDays = g.LeadTimeDays, itemCount = g.ItemCount }));
        });

        api.MapGet("/lookups/suppliers", async (string? source, string? search, int? take, ProviderFactory providers, CancellationToken ct) =>
        {
            var (m, error) = await LoadMaster(providers, source, ct);
            if (m is null) return Results.BadRequest(new { message = error });
            var q = m.Suppliers.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(search))
                q = q.Where(s => s.CardCode.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                 (s.CardName ?? "").Contains(search, StringComparison.OrdinalIgnoreCase));
            return Results.Ok(q.OrderBy(s => s.CardCode).Take(Math.Clamp(take ?? 50, 1, 500))
                .Select(s => new { cardCode = s.CardCode, cardName = s.CardName }));
        });

        var g = api.MapGroup("/masterdata");

        g.MapGet("/items", async (HttpContext ctx, ProviderFactory providers, IMasterStore store, SettingsService settings, CancellationToken ct) =>
        {
            var (rows, error) = await QueryRows(ctx.Request.Query, providers, store, settings, ct);
            if (rows is null) return Results.BadRequest(new { message = error });
            var page = Math.Max(1, Int(ctx.Request.Query["page"], 1));
            var size = Math.Clamp(Int(ctx.Request.Query["pageSize"], 100), 10, 500);
            return Results.Ok(new { total = rows.Count, page, pageSize = size, items = rows.Skip((page - 1) * size).Take(size).ToList() });
        });

        g.MapGet("/items/export", async (HttpContext ctx, ProviderFactory providers, IMasterStore store, SettingsService settings, CancellationToken ct) =>
        {
            var (rows, error) = await QueryRows(ctx.Request.Query, providers, store, settings, ct);
            if (rows is null) return Results.BadRequest(new { message = error });
            var sb = new StringBuilder();
            sb.AppendLine("ItemCode,ItemName,Group,Procurement,Supplier,SupplierName,LeadTimeDays,LeadTimeSource,PlanningMethod,LotSizing,MinOrderQty,OrderMultiple,SafetyStock,ReorderPoint,MaxStockLevel,FixedLotSize,OverriddenFields");
            foreach (var r in rows)
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(r.ItemCode), Csv(r.ItemName), Csv(r.GroupName ?? r.GroupCode), r.Procurement.ToString(), Csv(r.Effective.Supplier),
                    Csv(r.Effective.SupplierName), r.LeadTime.Days.ToString(), r.LeadTime.Source, r.Effective.PlanningMethod.ToString(),
                    r.Effective.LotSizing.ToString(), N(r.Effective.MinOrderQty), N(r.Effective.OrderMultiple), N(r.Effective.SafetyStock),
                    N(r.Effective.ReorderPoint), N(r.Effective.MaxStockLevel), N(r.Effective.FixedLotSize), Csv(string.Join(" ", r.OverriddenFields)),
                }));
            // UTF-8 with a BOM so Excel opens accented names correctly.
            var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", "master-data-items.csv");
        });

        // Push the planner's overrides to the SAP B1 item master. Preview reads SAP B1 and changes nothing. Admin only.
        g.MapPost("/items/push-to-sap/preview", async (PushPreviewRequest req, IMasterStore store, ItemPushService push, CancellationToken ct) =>
        {
            var codes = (req?.ItemCodes ?? new()).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (codes.Count == 0) return Results.BadRequest(new { message = "Select at least one item." });
            if (codes.Count > 200) return Results.BadRequest(new { message = "At most 200 items can be pushed at once." });
            try { return Results.Ok(await push.PreviewAsync(await store.GetItemOverridesAsync(ct), codes, ct)); }
            catch (PushException e) { return Results.Json(new { message = e.Message }, statusCode: e.Status); }
        }).RequireAuthorization("Admin");

        g.MapPost("/items/push-to-sap", async (PushExecuteRequest req, HttpContext ctx, IMasterStore store, ISapPushLog pushLog, ItemPushService push,
            ILogger<ItemPushService> log, CancellationToken ct) =>
        {
            var user = ctx.User.Identity?.Name ?? "";
            try
            {
                var result = await push.ExecuteAsync(req, store, new PushContext(pushLog, user), ct);
                log.LogInformation("Item master push by {User}: {Pushed} item(s) pushed, {Failed} failed", user, result.Pushed, result.Failed);
                return Results.Ok(result);
            }
            catch (PushException e) { return Results.Json(new { message = e.Message }, statusCode: e.Status); }
        }).RequireAuthorization("Admin");

        g.MapPut("/items/{itemCode}", async (string itemCode, string? source, ItemOverride body, HttpContext ctx,
            ProviderFactory providers, IMasterStore store, SettingsService settings, CancellationToken ct) =>
        {
            var (m, error) = await LoadMaster(providers, source, ct);
            if (m is null) return Results.BadRequest(new { message = error });
            var item = m.Items.FirstOrDefault(i => string.Equals(i.Plan.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase));
            if (item is null) return Results.NotFound(new { message = $"Item '{itemCode}' was not found in the data source." });

            body.ItemCode = item.Plan.ItemCode;
            if (Validate(body, m) is { } bad) return Results.BadRequest(new { message = bad });
            Stamp(body, ctx);
            await store.SaveItemsAsync(new[] { body }, ct);
            return Results.Ok(await OneRow(item.Plan.ItemCode, m, store, settings, ct));
        });

        g.MapPost("/items/bulk", async (string? source, BulkRequest req, HttpContext ctx, ProviderFactory providers,
            IMasterStore store, CancellationToken ct) =>
        {
            if (req.ItemCodes is not { Length: > 0 }) return Results.BadRequest(new { message = "Select at least one item." });
            var (m, error) = await LoadMaster(providers, source, ct);
            if (m is null) return Results.BadRequest(new { message = error });
            var known = m.Items.Select(i => i.Plan.ItemCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = req.ItemCodes.Where(c => !known.Contains(c)).Take(3).ToList();
            if (missing.Count > 0) return Results.BadRequest(new { message = $"Unknown item(s): {string.Join(", ", missing)}." });
            if (req.Set is not null && Validate(req.Set, m) is { } bad) return Results.BadRequest(new { message = bad });
            foreach (var name in req.Clear ?? Array.Empty<string>())
                if (!Fields.Contains(name)) return Results.BadRequest(new { message = $"Unknown field '{name}'." });

            var existing = await store.GetItemOverridesAsync(ct);
            var changed = new List<ItemOverride>();
            foreach (var code in req.ItemCodes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var o = existing.TryGetValue(code, out var e) ? e.Clone() : new ItemOverride { ItemCode = known.First(k => string.Equals(k, code, StringComparison.OrdinalIgnoreCase)) };
                if (req.Set is not null) CopySet(req.Set, o);
                foreach (var name in req.Clear ?? Array.Empty<string>()) ClearField(o, name);
                Stamp(o, ctx);
                changed.Add(o);
            }
            await store.SaveItemsAsync(changed, ct);
            return Results.Ok(new { updated = changed.Count });
        });

        g.MapDelete("/items/{itemCode}", async (string itemCode, IMasterStore store, CancellationToken ct) =>
        {
            await store.DeleteItemAsync(itemCode, ct);
            return Results.NoContent();
        });

        g.MapGet("/suppliers", async (HttpContext ctx, ProviderFactory providers, IMasterStore store, SettingsService settings, CancellationToken ct) =>
        {
            var q = ctx.Request.Query;
            var (m, error) = await LoadMaster(providers, q["source"], ct);
            if (m is null) return Results.BadRequest(new { message = error });
            var rows = await SupplierRows(m, store, settings, ct);

            var search = (string?)q["search"];
            if (!string.IsNullOrWhiteSpace(search))
                rows = rows.Where(r => r.CardCode.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                       (r.CardName ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
            rows = ((string?)q["filter"])?.ToLowerInvariant() switch
            {
                "withleadtime" => rows.Where(r => r.LeadTimeDays is not null).ToList(),
                "withoutleadtime" => rows.Where(r => r.LeadTimeDays is null).ToList(),
                "inuse" => rows.Where(r => r.ItemCount > 0).ToList(),
                _ => rows,
            };
            var page = Math.Max(1, Int(q["page"], 1));
            var size = Math.Clamp(Int(q["pageSize"], 100), 10, 500);
            return Results.Ok(new { total = rows.Count, page, pageSize = size, suppliers = rows.Skip((page - 1) * size).Take(size).ToList() });
        });

        // Bulk lead-time import. dryRun=true checks and reports without saving; Cobalt-side only, SAP B1 is never written.
        // A blank lead time or blank notes cell leaves that field as it is.
        g.MapPost("/suppliers/import", async (string? source, bool? dryRun, SupplierImportRequest req, HttpContext ctx,
            ProviderFactory providers, IMasterStore store, CancellationToken ct) =>
        {
            const int MaxRows = 5000;
            if (req?.Rows is null || req.Rows.Count == 0) return Results.BadRequest(new { message = "The file has no rows." });
            if (req.Rows.Count > MaxRows) return Results.BadRequest(new { message = $"At most {MaxRows} rows can be imported at once." });
            var (m, error) = await LoadMaster(providers, source, ct);
            if (m is null) return Results.BadRequest(new { message = error });

            var known = m.Suppliers.ToDictionary(x => x.CardCode, StringComparer.OrdinalIgnoreCase);
            var current = await store.GetSupplierSettingsAsync(ct);
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var results = new List<SupplierImportResult>();
            var toSave = new List<SupplierSetting>();
            var user = ctx.User.Identity?.Name ?? "";

            foreach (var r in req.Rows)
            {
                var code = (r.CardCode ?? "").Trim();
                SupplierImportResult Err(string msg) => new(r.Line, code, "error", msg, null, null);
                if (code.Length == 0) { results.Add(Err("The supplier code is empty.")); continue; }
                if (!known.TryGetValue(code, out var sup)) { results.Add(Err("This supplier is not in the data source.")); continue; }
                if (seen.TryGetValue(sup.CardCode, out var first)) { results.Add(Err($"Duplicate of line {first}: only the first one is used.")); continue; }
                seen[sup.CardCode] = r.Line;

                current.TryGetValue(sup.CardCode, out var before);
                int? newLt = before?.LeadTimeDays;
                var text = (r.LeadTimeDays ?? "").Trim();
                if (text.Length > 0)
                {
                    if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var days) || days < 0 || days > 3650)
                    { results.Add(Err($"Lead time '{text}' must be a whole number from 0 to 3650.")); continue; }
                    newLt = days;
                }
                var notes = (r.Notes ?? "").Trim();
                if (notes.Length > 1000) { results.Add(Err("The note is longer than 1000 characters.")); continue; }
                var newNotes = notes.Length > 0 ? notes : before?.Notes ?? "";

                if (newLt == before?.LeadTimeDays && newNotes == (before?.Notes ?? ""))
                { results.Add(new(r.Line, sup.CardCode, "same", "Already has these values.", before?.LeadTimeDays, newLt)); continue; }

                results.Add(new(r.Line, sup.CardCode, "change", before is null ? "New lead time." : "Updated.", before?.LeadTimeDays, newLt));
                toSave.Add(new SupplierSetting { CardCode = sup.CardCode, LeadTimeDays = newLt, Notes = newNotes, UpdatedBy = user, UpdatedAt = DateTimeOffset.UtcNow });
            }

            var dry = dryRun ?? false;
            if (!dry) foreach (var s in toSave) await store.SaveSupplierAsync(s, ct);
            return Results.Ok(new
            {
                dryRun = dry, applied = dry ? 0 : toSave.Count, willChange = toSave.Count,
                same = results.Count(x => x.Status == "same"), errors = results.Count(x => x.Status == "error"), results,
            });
        });

        g.MapPut("/suppliers/{cardCode}", async (string cardCode, string? source, SupplierEdit body, HttpContext ctx,
            ProviderFactory providers, IMasterStore store, SettingsService settings, CancellationToken ct) =>
        {
            var (m, error) = await LoadMaster(providers, source, ct);
            if (m is null) return Results.BadRequest(new { message = error });
            var sup = m.Suppliers.FirstOrDefault(s => string.Equals(s.CardCode, cardCode, StringComparison.OrdinalIgnoreCase));
            if (sup is null) return Results.NotFound(new { message = $"Supplier '{cardCode}' was not found in the data source." });
            if (body.LeadTimeDays is < 0 or > 3650) return Results.BadRequest(new { message = "Lead time must be between 0 and 3650 days." });

            await store.SaveSupplierAsync(new SupplierSetting
            {
                CardCode = sup.CardCode, LeadTimeDays = body.LeadTimeDays, Notes = (body.Notes ?? "").Trim(),
                UpdatedBy = ctx.User.Identity?.Name ?? "", UpdatedAt = DateTimeOffset.UtcNow,
            }, ct);
            var rows = await SupplierRows(m, store, settings, ct);
            return Results.Ok(rows.First(r => string.Equals(r.CardCode, sup.CardCode, StringComparison.OrdinalIgnoreCase)));
        });
    }

    // ---- Rows -------------------------------------------------------------------------------------------------

    internal static async Task<(MasterData? Master, string? Error)> LoadMaster(ProviderFactory providers, string? source, CancellationToken ct)
    {
        var (p, error) = providers.CreateMaster(source);
        return p is null ? (null, error) : (await p.LoadMasterAsync(ct), null);
    }

    private static LeadTimeCriteria Rule(SettingsService settings)
    {
        var p = settings.Get().Planning;
        var c = new LeadTimeCriteria
        {
            Mode = p.LeadTimeMode == "Longest" ? LeadTimeMode.Longest : LeadTimeMode.FirstAvailable,
            DefaultDays = p.DefaultLeadTimeDays,
            Priority = p.LeadTimePriority.Select(n => Enum.Parse<LeadTimeSourceKind>(n, true)).ToList(),
        };
        return c;
    }

    private static MasterItemRow Row(MasterItem mi, ItemOverride? o, IReadOnlyDictionary<string, SupplierSetting> sup,
        IReadOnlyDictionary<string, string?> supplierNames, LeadTimeCriteria rule, IReadOnlyDictionary<string, int> supplierCounts)
    {
        var b1 = mi.Plan;
        var eff = PlanningOverlay.ApplyToItem(b1, o, sup, rule);
        string? Name(string? code) => code is not null && supplierNames.TryGetValue(code, out var n) ? n : null;

        PlanValues Values(PlanItem p, int? leadTime) => new(p.PlanningMethod, p.LotSizing, leadTime, p.GroupLeadTimeDays, p.MinOrderQty,
            p.OrderMultiple, p.SafetyStock, p.ReorderPoint, p.MaxStockLevel, p.FixedLotSize, p.PeriodOfSupplyDays,
            p.PreferredVendor, Name(p.PreferredVendor));

        int? itemDays = o?.LeadTimeDays ?? (b1.LeadTimeDays > 0 ? b1.LeadTimeDays : null);
        int? groupDays = b1.GroupLeadTimeDays is > 0 ? b1.GroupLeadTimeDays : null;
        int? supplierDays = b1.Procurement == ProcurementType.Buy && eff.PreferredVendor is not null && sup.TryGetValue(eff.PreferredVendor, out var s)
            ? s.LeadTimeDays : null;

        return new MasterItemRow(b1.ItemCode, b1.ItemName, mi.GroupCode, mi.GroupName, b1.Procurement,
            Values(b1, b1.LeadTimeDays > 0 ? b1.LeadTimeDays : null), o,
            Values(eff, eff.LeadTimeDays), new LeadTimeInfo(eff.LeadTimeDays, eff.LeadTimeSource ?? "None", supplierDays, itemDays, groupDays,
                b1.Procurement == ProcurementType.Buy && eff.PreferredVendor is not null ? supplierCounts.GetValueOrDefault(eff.PreferredVendor) : 0),
            o?.OverriddenFields() ?? Array.Empty<string>());
    }

    /// <summary>How many purchased items each supplier is the effective supplier of (so the UI can warn before a supplier-wide edit).</summary>
    private static Dictionary<string, int> SupplierCounts(MasterData m, IReadOnlyDictionary<string, ItemOverride> ov,
        IReadOnlyDictionary<string, SupplierSetting> sup, LeadTimeCriteria rule) =>
        m.Items.Select(mi => PlanningOverlay.ApplyToItem(mi.Plan, ov.TryGetValue(mi.Plan.ItemCode, out var o) ? o : null, sup, rule))
            .Where(i => i.Procurement == ProcurementType.Buy && i.PreferredVendor is not null)
            .GroupBy(i => i.PreferredVendor!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

    /// <summary>One item's effective planning data, or null when the code is not in the master data (used by the AI assistant, read-only).</summary>
    internal static async Task<MasterItemRow?> ItemRowOrNullAsync(string code, MasterData m, IMasterStore store, SettingsService settings, CancellationToken ct)
    {
        if (!m.Items.Any(i => string.Equals(i.Plan.ItemCode, code, StringComparison.OrdinalIgnoreCase))) return null;
        return await OneRow(code, m, store, settings, ct);
    }

    private static async Task<MasterItemRow> OneRow(string code, MasterData m, IMasterStore store, SettingsService settings, CancellationToken ct)
    {
        var ov = await store.GetItemOverridesAsync(ct);
        var sup = await store.GetSupplierSettingsAsync(ct);
        var names = m.Suppliers.ToDictionary(s => s.CardCode, s => s.CardName, StringComparer.OrdinalIgnoreCase);
        var mi = m.Items.First(i => string.Equals(i.Plan.ItemCode, code, StringComparison.OrdinalIgnoreCase));
        var rule = Rule(settings);
        return Row(mi, ov.TryGetValue(mi.Plan.ItemCode, out var o) ? o : null, sup, names!, rule, SupplierCounts(m, ov, sup, rule));
    }

    private static async Task<(List<MasterItemRow>? Rows, string? Error)> QueryRows(IQueryCollection q, ProviderFactory providers,
        IMasterStore store, SettingsService settings, CancellationToken ct)
    {
        var (m, error) = await LoadMaster(providers, q["source"], ct);
        if (m is null) return (null, error);
        var ov = await store.GetItemOverridesAsync(ct);
        var sup = await store.GetSupplierSettingsAsync(ct);
        var names = m.Suppliers.ToDictionary(s => s.CardCode, s => s.CardName, StringComparer.OrdinalIgnoreCase);
        var rule = Rule(settings);

        var counts = SupplierCounts(m, ov, sup, rule);
        IEnumerable<MasterItemRow> rows = m.Items.Select(mi => Row(mi, ov.TryGetValue(mi.Plan.ItemCode, out var o) ? o : null, sup, names!, rule, counts));

        string? search = q["search"], group = q["group"], supplier = q["supplier"], procurement = q["procurement"], filter = q["filter"];
        if (!string.IsNullOrWhiteSpace(search))
            rows = rows.Where(r => r.ItemCode.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                   (r.ItemName ?? "").Contains(search, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(group)) rows = rows.Where(r => string.Equals(r.GroupCode, group, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(supplier)) rows = rows.Where(r => string.Equals(r.Effective.Supplier, supplier, StringComparison.OrdinalIgnoreCase));
        if (Enum.TryParse<ProcurementType>(procurement, true, out var proc)) rows = rows.Where(r => r.Procurement == proc);
        rows = filter?.ToLowerInvariant() switch
        {
            "overridden" => rows.Where(r => r.OverriddenFields.Count > 0),
            "nosupplier" => rows.Where(r => r.Procurement == ProcurementType.Buy && string.IsNullOrWhiteSpace(r.Effective.Supplier)),
            "noleadtime" => rows.Where(r => r.LeadTime.Source == "None"),
            _ => rows,
        };

        var desc = string.Equals(q["dir"], "desc", StringComparison.OrdinalIgnoreCase);
        Func<MasterItemRow, object> key = ((string?)q["sort"])?.ToLowerInvariant() switch
        {
            "itemname" => r => r.ItemName ?? "",
            "leadtime" => r => r.LeadTime.Days,
            "supplier" => r => r.Effective.Supplier ?? "",
            "group" => r => r.GroupName ?? r.GroupCode ?? "",
            _ => r => r.ItemCode,
        };
        rows = desc ? rows.OrderByDescending(key).ThenBy(r => r.ItemCode) : rows.OrderBy(key).ThenBy(r => r.ItemCode);
        return (rows.ToList(), null);
    }

    private static async Task<List<SupplierRow>> SupplierRows(MasterData m, IMasterStore store, SettingsService settings, CancellationToken ct)
    {
        var ov = await store.GetItemOverridesAsync(ct);
        var sup = await store.GetSupplierSettingsAsync(ct);
        var rule = Rule(settings);
        var used = m.Items.Select(mi => PlanningOverlay.ApplyToItem(mi.Plan, ov.TryGetValue(mi.Plan.ItemCode, out var o) ? o : null, sup, rule))
            .Where(i => i.Procurement == ProcurementType.Buy && i.PreferredVendor is not null)
            .GroupBy(i => i.PreferredVendor!, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

        return m.Suppliers.OrderBy(s => s.CardCode).Select(s =>
        {
            sup.TryGetValue(s.CardCode, out var st);
            return new SupplierRow(s.CardCode, s.CardName, st?.LeadTimeDays, st?.Notes ?? "", used.GetValueOrDefault(s.CardCode),
                string.IsNullOrEmpty(st?.UpdatedBy) ? null : st!.UpdatedBy, st is null ? null : st.UpdatedAt);
        }).ToList();
    }

    // ---- Validation and field handling -----------------------------------------------------------------------

    private static readonly HashSet<string> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        "planningMethod", "lotSizing", "leadTimeDays", "minOrderQty", "orderMultiple", "safetyStock", "reorderPoint",
        "maxStockLevel", "fixedLotSize", "periodOfSupplyDays", "supplier",
    };

    private static string? Validate(ItemOverride o, MasterData m)
    {
        if (o.LeadTimeDays is < 0 or > 3650) return "Lead time must be between 0 and 3650 days.";
        if (o.PeriodOfSupplyDays is < 0 or > 3650) return "Period of supply must be between 0 and 3650 days.";
        foreach (var (name, v) in new (string, decimal?)[]
        {
            ("Min order quantity", o.MinOrderQty), ("Order multiple", o.OrderMultiple), ("Safety stock", o.SafetyStock),
            ("Reorder point", o.ReorderPoint), ("Max stock level", o.MaxStockLevel), ("Fixed lot size", o.FixedLotSize),
        })
            if (v is < 0) return $"{name} cannot be negative.";
        if (!string.IsNullOrWhiteSpace(o.Supplier))
        {
            o.Supplier = o.Supplier.Trim();
            if (!m.Suppliers.Any(s => string.Equals(s.CardCode, o.Supplier, StringComparison.OrdinalIgnoreCase)))
                return $"Supplier '{o.Supplier}' is not a known supplier in the data source.";
            o.Supplier = m.Suppliers.First(s => string.Equals(s.CardCode, o.Supplier, StringComparison.OrdinalIgnoreCase)).CardCode;
        }
        else o.Supplier = null;
        o.Notes = (o.Notes ?? "").Trim();
        return null;
    }

    private static void Stamp(ItemOverride o, HttpContext ctx) { o.UpdatedBy = ctx.User.Identity?.Name ?? ""; o.UpdatedAt = DateTimeOffset.UtcNow; }

    /// <summary>Copy only the fields that were set in a bulk edit.</summary>
    private static void CopySet(ItemOverride from, ItemOverride to)
    {
        if (from.PlanningMethod is not null) to.PlanningMethod = from.PlanningMethod;
        if (from.LotSizing is not null) to.LotSizing = from.LotSizing;
        if (from.LeadTimeDays is not null) to.LeadTimeDays = from.LeadTimeDays;
        if (from.MinOrderQty is not null) to.MinOrderQty = from.MinOrderQty;
        if (from.OrderMultiple is not null) to.OrderMultiple = from.OrderMultiple;
        if (from.SafetyStock is not null) to.SafetyStock = from.SafetyStock;
        if (from.ReorderPoint is not null) to.ReorderPoint = from.ReorderPoint;
        if (from.MaxStockLevel is not null) to.MaxStockLevel = from.MaxStockLevel;
        if (from.FixedLotSize is not null) to.FixedLotSize = from.FixedLotSize;
        if (from.PeriodOfSupplyDays is not null) to.PeriodOfSupplyDays = from.PeriodOfSupplyDays;
        if (!string.IsNullOrWhiteSpace(from.Supplier)) to.Supplier = from.Supplier;
        if (!string.IsNullOrWhiteSpace(from.Notes)) to.Notes = from.Notes;
    }

    private static void ClearField(ItemOverride o, string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "planningmethod": o.PlanningMethod = null; break;
            case "lotsizing": o.LotSizing = null; break;
            case "leadtimedays": o.LeadTimeDays = null; break;
            case "minorderqty": o.MinOrderQty = null; break;
            case "ordermultiple": o.OrderMultiple = null; break;
            case "safetystock": o.SafetyStock = null; break;
            case "reorderpoint": o.ReorderPoint = null; break;
            case "maxstocklevel": o.MaxStockLevel = null; break;
            case "fixedlotsize": o.FixedLotSize = null; break;
            case "periodofsupplydays": o.PeriodOfSupplyDays = null; break;
            case "supplier": o.Supplier = null; break;
        }
    }

    private static int Int(string? v, int fallback) => int.TryParse(v, out var n) ? n : fallback;
    private static string N(decimal v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    private static string Csv(string? v)
    {
        v ??= "";
        // Neutralise spreadsheet formula injection as well as quoting.
        if (v.Length > 0 && "=+-@".Contains(v[0])) v = "'" + v;
        return v.Contains(',') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
