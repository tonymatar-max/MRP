using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CobaltMrp.Domain;

namespace CobaltMrp.Api.Ai;

/// <summary>Item master data in the compact form the model may see (no secrets, no connection details).</summary>
public sealed record ItemMaster(
    string ItemCode, string? Name, string? Group, string Procurement, string? Supplier, string? SupplierName,
    int LeadTimeDays, string LeadTimeSource, string PlanningMethod, string LotSizing, decimal MinOrderQty,
    decimal OrderMultiple, decimal SafetyStock, decimal ReorderPoint, decimal MaxStockLevel, decimal FixedLotSize,
    IReadOnlyList<string> OverriddenFields);

public delegate Task<ItemMaster?> ItemLookup(string itemCode, CancellationToken ct);

/// <summary>
/// The tools the assistant may call. <b>Every tool is read-only</b>: the toolbox is built from a saved run and a master-data
/// lookup, and has no reference to any store, settings, Service Layer or user data, so it cannot create, release, execute,
/// move or edit anything. Results are compact (capped rows, rounded numbers) because they are sent to the model.
/// </summary>
public sealed class RunToolbox
{
    public const int DefaultRows = 20;
    public const int MaxRows = 40;
    public const int MaxResultChars = 9000;

    public static readonly IReadOnlyList<string> ToolNames = new[]
    {
        "get_run_summary", "list_planned_orders", "get_pegging", "list_exceptions", "get_item", "get_calendar_events",
    };

    private readonly MrpRun _run;
    private readonly ItemLookup? _items;

    public RunToolbox(MrpRun run, ItemLookup? items = null) { _run = run; _items = items; }

    // ---- Schemas sent to the model ------------------------------------------------------------------------------

    public static JsonArray Schemas() => new(
        Tool("get_run_summary", "Overview of the saved run: criteria, counts by order type and exception type, planning level, warehouses, late releases, capacity.",
            new JsonObject()),
        Tool("list_planned_orders", "List planned orders (purchase orders/requests, transfer requests, production orders), earliest first. All filters optional.",
            Props(("item", "string", "Item code (exact or part of it)"),
                  ("type", "string", "PurchaseOrder, PurchaseRequest, TransferRequest or ProductionOrder"),
                  ("warehouse", "string", "Warehouse code (for transfers: matches the receiving or the sending warehouse)"),
                  ("limit", "integer", $"Rows to return, default {DefaultRows}, max {MaxRows}"))),
        Tool("get_pegging", "Why a planned order exists: the demand it covers (sales orders, forecast, dependent demand, safety stock, transfers). Give plannedOrderId or itemCode.",
            Props(("plannedOrderId", "string", "Planned order id (GUID) from list_planned_orders"),
                  ("itemCode", "string", "Item code: pegging of that item's planned orders"))),
        Tool("list_exceptions", "List planner action messages (Expedite, Defer, Cancel, PastDue, ReleaseInPast, CapacityOverload, NotPlanned), most urgent first.",
            Props(("type", "string", "Exception type"), ("item", "string", "Item code (exact or part of it)"),
                  ("warehouse", "string", "Warehouse code"), ("status", "string", "open, done or all (default all)"),
                  ("limit", "integer", $"Rows to return, default {DefaultRows}, max {MaxRows}"))),
        Tool("get_item", "One item: master data (supplier, lead time, lot sizing, safety stock...) plus what the run holds for it: demand, open supply, planned orders, exceptions.",
            Props(("itemCode", "string", "Exact item code")), "itemCode"),
        Tool("get_calendar_events", "Dated events of the run (sales orders due, forecasts, open supply, planned releases and receipts, exceptions), oldest first.",
            Props(("item", "string", "Item code (exact or part of it)"), ("from", "string", "Start date yyyy-MM-dd (default: run start)"),
                  ("to", "string", "End date yyyy-MM-dd (default: run end; at most about a year)"),
                  ("kinds", "string", "Comma list: Demand, Forecast, Supply, PlannedRelease, PlannedDue, Exception, CapacityOverload"),
                  ("limit", "integer", $"Rows to return, default {DefaultRows}, max {MaxRows}"))));

    private static JsonObject Props(params (string Name, string Type, string Desc)[] p)
    {
        var o = new JsonObject();
        foreach (var (n, t, d) in p) o[n] = new JsonObject { ["type"] = t, ["description"] = d };
        return o;
    }

    private static JsonObject Tool(string name, string description, JsonObject props, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name, ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object", ["properties"] = props,
                ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            },
        },
    };

    // ---- Execution ------------------------------------------------------------------------------------------------

    /// <summary>Runs one tool and returns compact JSON text. Never throws for bad arguments: the error goes back to the model.</summary>
    public async Task<string> ExecuteAsync(string name, string? argsJson, CancellationToken ct)
    {
        JsonObject args;
        try { args = string.IsNullOrWhiteSpace(argsJson) ? new JsonObject() : JsonNode.Parse(argsJson) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return Err("The tool arguments were not valid JSON."); }

        try
        {
            var result = name switch
            {
                "get_run_summary" => RunSummary(),
                "list_planned_orders" => ListPlannedOrders(args),
                "get_pegging" => Pegging(args),
                "list_exceptions" => ListExceptions(args),
                "get_item" => await GetItemAsync(args, ct),
                "get_calendar_events" => CalendarEvents(args),
                _ => Error($"Unknown tool '{name}'. Available: {string.Join(", ", ToolNames)}."),
            };
            var text = result.ToJsonString();
            return text.Length > MaxResultChars ? Err("The result was too large. Narrow it with item, type, warehouse or a smaller limit.") : text;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Err("The lookup failed."); }
    }

    /// <summary>"item=TUB-01, limit=10": a short description of a call for the user (never the full prompt).</summary>
    public static string Describe(string argsJson)
    {
        try
        {
            if (JsonNode.Parse(argsJson) is not JsonObject o || o.Count == 0) return "";
            var s = string.Join(", ", o.Select(kv => $"{kv.Key}={Trunc(kv.Value?.ToString() ?? "", 40)}"));
            return Trunc(s, 120);
        }
        catch (JsonException) { return ""; }
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private static string Err(string message) => Error(message).ToJsonString();
    private static JsonObject Error(string message) => new() { ["error"] = message };

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    /// <summary>Round to 3 decimals and drop trailing zeros so the model sees 12.5, not 12.500000.</summary>
    public static JsonNode Num(decimal d)
    {
        var r = Math.Round(d, 3, MidpointRounding.AwayFromZero);
        return r == Math.Truncate(r) ? JsonValue.Create((long)r) : JsonValue.Create((double)r);
    }

    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static JsonNode? D(DateOnly? d) => d is null ? null : JsonValue.Create(D(d.Value));

    private static string? Str(JsonObject a, string key) =>
        a[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static int Limit(JsonObject a)
    {
        if (a["limit"] is JsonValue v)
        {
            if (v.TryGetValue<int>(out var n)) return Math.Clamp(n, 1, MaxRows);
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, out n)) return Math.Clamp(n, 1, MaxRows);
        }
        return DefaultRows;
    }

    private static bool ItemMatch(string code, string filter) =>
        string.Equals(code, filter, StringComparison.OrdinalIgnoreCase) || code.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool TryType(string text, out PlannedOrderType type)
    {
        var t = text.Replace(" ", "").Replace("_", "").Replace("-", "");
        switch (t.ToUpperInvariant())
        {
            case "PO": type = PlannedOrderType.PurchaseOrder; return true;
            case "PR": type = PlannedOrderType.PurchaseRequest; return true;
            case "TR": case "TRANSFER": type = PlannedOrderType.TransferRequest; return true;
            case "PRODUCTION": case "MAKE": type = PlannedOrderType.ProductionOrder; return true;
        }
        return Enum.TryParse(t, true, out type) && Enum.IsDefined(type);
    }

    private JsonObject OrderRow(PlannedOrder o)
    {
        var r = new JsonObject
        {
            ["id"] = o.Id.ToString(), ["item"] = o.ItemCode, ["type"] = o.Type.ToString(), ["warehouse"] = o.WarehouseCode,
            ["qty"] = Num(o.Quantity), ["due"] = D(o.DueDate), ["release"] = D(o.ReleaseDate), ["leadTimeDays"] = o.LeadTimeDays,
        };
        if (o.FromWarehouse is not null) r["fromWarehouse"] = o.FromWarehouse;
        if (!string.IsNullOrWhiteSpace(o.VendorCode)) r["supplier"] = o.VendorCode;
        if (o.LeadTimeSource is not null) r["leadTimeSource"] = o.LeadTimeSource;
        if (o.ReleaseDate < _run.HorizonStart) r["releaseBeforeRunStart"] = true;
        if (o.Firmed) r["firmed"] = true;
        if (!string.IsNullOrEmpty(o.B1DocEntry)) r["sapDocEntry"] = o.B1DocEntry;
        return r;
    }

    private static JsonObject Counts<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var o = new JsonObject();
        foreach (var g in items.GroupBy(key).OrderBy(g => g.Key, StringComparer.Ordinal)) o[g.Key] = g.Count();
        return o;
    }

    // ---- Tools ----------------------------------------------------------------------------------------------------

    private JsonObject RunSummary()
    {
        var c = RunCriteria.FromJson(_run.CriteriaJson);
        var orders = _run.PlannedOrders;
        var whs = orders.Select(o => o.WarehouseCode)
            .Concat(orders.Where(o => o.FromWarehouse is not null).Select(o => o.FromWarehouse!))
            .Concat(c?.Scope.Warehouses ?? new()).Distinct(StringComparer.OrdinalIgnoreCase).Order().Take(40).ToList();
        var open = _run.Exceptions.Count(e => e.ActionStatus != "Done");
        var overloaded = _run.CapacityLoads.Where(l => l.Overloaded).ToList();

        var o = new JsonObject
        {
            ["runId"] = _run.Id.ToString(), ["runAt"] = _run.RunAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
            ["runBy"] = _run.RunBy,
            ["horizon"] = new JsonObject { ["start"] = D(_run.HorizonStart), ["end"] = D(_run.HorizonEnd) },
            ["bucketDays"] = _run.BucketDays,
        };
        if (c is not null)
        {
            o["criteria"] = new JsonObject
            {
                ["source"] = c.Source, ["planningLevel"] = c.Level.ToString(),
                ["purchasingDocument"] = c.Purchasing.Document.ToString(), ["transfersEnabled"] = c.Transfers.Enabled,
                ["leadTimeMode"] = c.LeadTime.Mode.ToString(),
                ["leadTimePriority"] = string.Join(" > ", c.LeadTime.Priority),
                ["warehousesInScope"] = c.Scope.Warehouses.Count == 0 ? "all" : string.Join(", ", c.Scope.Warehouses.Take(20)),
            };
        }
        o["warehouses"] = new JsonArray(whs.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray());
        o["plannedOrders"] = new JsonObject
        {
            ["total"] = orders.Count, ["itemsPlanned"] = orders.Select(x => x.ItemCode).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ["byType"] = Counts(orders, x => x.Type.ToString()),
            ["byWarehouse"] = Counts(orders, x => x.WarehouseCode),
            ["maxLowLevelCode"] = orders.Count == 0 ? 0 : orders.Max(x => x.LowLevelCode),
            ["releaseDateBeforeRunStart"] = orders.Count(x => x.ReleaseDate < _run.HorizonStart),
            ["firmed"] = orders.Count(x => x.Firmed),
            ["releasedToSap"] = orders.Count(x => !string.IsNullOrEmpty(x.B1DocEntry)),
        };
        o["exceptions"] = new JsonObject
        {
            ["total"] = _run.Exceptions.Count, ["open"] = open, ["byType"] = Counts(_run.Exceptions, x => x.Type.ToString()),
        };
        o["capacity"] = new JsonObject
        {
            ["overloadedBuckets"] = overloaded.Count,
            ["overloadedWorkCenters"] = new JsonArray(overloaded.Select(l => l.WorkCenterCode).Distinct().Order().Take(10)
                .Select(w => (JsonNode)JsonValue.Create(w)!).ToArray()),
        };
        return o;
    }

    private JsonObject ListPlannedOrders(JsonObject a)
    {
        var item = Str(a, "item"); var wh = Str(a, "warehouse"); var typeText = Str(a, "type");
        PlannedOrderType? type = null;
        if (typeText is not null)
        {
            if (!TryType(typeText, out var t)) return Error($"Unknown type '{Trunc(typeText, 30)}'. Use PurchaseOrder, PurchaseRequest, TransferRequest or ProductionOrder.");
            type = t;
        }
        var q = _run.PlannedOrders.AsEnumerable();
        if (item is not null) q = q.Where(o => ItemMatch(o.ItemCode, item));
        if (type is not null) q = q.Where(o => o.Type == type);
        if (wh is not null) q = q.Where(o => Same(o.WarehouseCode, wh) || Same(o.FromWarehouse, wh));
        var all = q.OrderBy(o => o.ReleaseDate).ThenBy(o => o.DueDate).ThenBy(o => o.ItemCode, StringComparer.Ordinal).ToList();
        var limit = Limit(a);
        return new JsonObject
        {
            ["total"] = all.Count, ["returned"] = Math.Min(all.Count, limit), ["truncated"] = all.Count > limit,
            ["totalQty"] = Num(all.Sum(o => o.Quantity)),
            ["orders"] = new JsonArray(all.Take(limit).Select(o => (JsonNode)OrderRow(o)).ToArray()),
        };
    }

    private JsonObject Link(PeggingLink l)
    {
        var o = new JsonObject { ["source"] = l.DemandSource.ToString(), ["qty"] = Num(l.Quantity), ["due"] = D(l.DemandDueDate) };
        if (!string.IsNullOrWhiteSpace(l.DemandDocKey)) o["document"] = l.DemandDocKey;
        if (!string.IsNullOrWhiteSpace(l.ParentItemCode)) o["parentItem"] = l.ParentItemCode;
        return o;
    }

    private JsonObject OrderWithLinks(PlannedOrder o, int maxLinks)
    {
        var links = _run.Pegging.Where(p => p.PlannedOrderId == o.Id).OrderBy(p => p.DemandDueDate).ToList();
        var r = OrderRow(o);
        r["peggedQty"] = Num(links.Sum(l => l.Quantity));
        r["linkCount"] = links.Count;
        r["links"] = new JsonArray(links.Take(maxLinks).Select(l => (JsonNode)Link(l)).ToArray());
        if (links.Count > maxLinks) r["linksTruncated"] = true;
        return r;
    }

    private JsonObject Pegging(JsonObject a)
    {
        var id = Str(a, "plannedOrderId"); var item = Str(a, "itemCode") ?? Str(a, "item");
        if (id is not null)
        {
            if (!Guid.TryParse(id, out var g)) return Error("plannedOrderId must be a GUID from list_planned_orders.");
            var o = _run.PlannedOrders.FirstOrDefault(x => x.Id == g);
            return o is null ? Error("No planned order with that id in this run.") : OrderWithLinks(o, 15);
        }
        if (item is null) return Error("Give plannedOrderId or itemCode.");
        var orders = _run.PlannedOrders.Where(o => Same(o.ItemCode, item)).OrderBy(o => o.DueDate).ToList();
        if (orders.Count == 0) return Error($"No planned orders for item '{Trunc(item, 40)}' in this run.");
        return new JsonObject
        {
            ["item"] = orders[0].ItemCode, ["orderCount"] = orders.Count, ["truncated"] = orders.Count > 8,
            ["orders"] = new JsonArray(orders.Take(8).Select(o => (JsonNode)OrderWithLinks(o, 8)).ToArray()),
        };
    }

    private static int Urgency(ExceptionType t) => t switch
    {
        ExceptionType.ReleaseInPast => 0, ExceptionType.PastDue => 1, ExceptionType.Expedite => 2,
        ExceptionType.CapacityOverload => 3, ExceptionType.Cancel => 4, ExceptionType.Defer => 5, _ => 6,
    };

    private static JsonObject ExceptionRow(ExceptionMessage e)
    {
        var r = new JsonObject
        {
            ["id"] = e.ExceptionId.ToString(), ["type"] = e.Type.ToString(), ["item"] = e.ItemCode,
            ["message"] = Trunc(e.Message, 220), ["qty"] = Num(e.Quantity), ["status"] = e.ActionStatus ?? "Open",
        };
        if (e.WarehouseCode is not null) r["warehouse"] = e.WarehouseCode;
        if (e.RelatedDate is not null) r["date"] = D(e.RelatedDate);
        if (e.CurrentDate is not null) r["currentDate"] = D(e.CurrentDate);
        if (e.ProposedDate is not null) r["proposedDate"] = D(e.ProposedDate);
        if (!string.IsNullOrWhiteSpace(e.SourceDocKey)) r["sapDocument"] = e.SourceDocKey;
        if (e.PlannedOrderId is not null) r["plannedOrderId"] = e.PlannedOrderId.ToString();
        return r;
    }

    private JsonObject ListExceptions(JsonObject a)
    {
        var item = Str(a, "item"); var wh = Str(a, "warehouse"); var typeText = Str(a, "type"); var status = Str(a, "status")?.ToLowerInvariant();
        ExceptionType? type = null;
        if (typeText is not null)
        {
            if (!Enum.TryParse<ExceptionType>(typeText.Replace(" ", "").Replace("_", ""), true, out var t) || !Enum.IsDefined(t))
                return Error($"Unknown type '{Trunc(typeText, 30)}'. Use Expedite, Defer, Cancel, PastDue, ReleaseInPast, CapacityOverload or NotPlanned.");
            type = t;
        }
        if (status is not (null or "open" or "done" or "all")) return Error("status must be open, done or all.");
        var q = _run.Exceptions.AsEnumerable();
        if (type is not null) q = q.Where(e => e.Type == type);
        if (item is not null) q = q.Where(e => ItemMatch(e.ItemCode, item));
        if (wh is not null) q = q.Where(e => Same(e.WarehouseCode, wh));
        if (status == "open") q = q.Where(e => e.ActionStatus != "Done");
        if (status == "done") q = q.Where(e => e.ActionStatus == "Done");
        var all = q.OrderBy(e => Urgency(e.Type)).ThenBy(e => e.RelatedDate ?? DateOnly.MaxValue).ThenBy(e => e.ItemCode, StringComparer.Ordinal).ToList();
        var limit = Limit(a);
        return new JsonObject
        {
            ["total"] = all.Count, ["returned"] = Math.Min(all.Count, limit), ["truncated"] = all.Count > limit,
            ["byType"] = Counts(all, e => e.Type.ToString()),
            ["exceptions"] = new JsonArray(all.Take(limit).Select(e => (JsonNode)ExceptionRow(e)).ToArray()),
        };
    }

    private static JsonObject EventTotals(IEnumerable<CalendarEvent> events)
    {
        var l = events.ToList();
        var o = new JsonObject { ["count"] = l.Count, ["qty"] = Num(l.Sum(e => e.Quantity)) };
        if (l.Count > 0) { o["earliest"] = D(l.Min(e => e.Date)); o["latest"] = D(l.Max(e => e.Date)); }
        return o;
    }

    private async Task<JsonObject> GetItemAsync(JsonObject a, CancellationToken ct)
    {
        var code = Str(a, "itemCode") ?? Str(a, "item");
        if (code is null) return Error("itemCode is required.");
        var orders = _run.PlannedOrders.Where(o => Same(o.ItemCode, code)).ToList();
        var events = _run.Events.Where(e => Same(e.ItemCode, code)).ToList();
        var exceptions = _run.Exceptions.Where(e => Same(e.ItemCode, code)).ToList();

        ItemMaster? m = null; var masterNote = "master data lookup is not available";
        if (_items is not null)
        {
            try { m = await _items(code, ct); masterNote = m is null ? "item not found in the master data" : "current SAP B1 data with Cobalt overrides (not as of the run)"; }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { masterNote = "master data could not be read"; }
        }
        if (m is null && orders.Count == 0 && events.Count == 0 && exceptions.Count == 0)
            return Error($"Item '{Trunc(code, 40)}' was not found in the master data or in this run.");

        var o = new JsonObject { ["item"] = m?.ItemCode ?? orders.FirstOrDefault()?.ItemCode ?? code };
        if (m is not null)
        {
            var md = new JsonObject
            {
                ["name"] = m.Name, ["group"] = m.Group, ["procurement"] = m.Procurement, ["supplier"] = m.Supplier,
                ["supplierName"] = m.SupplierName, ["leadTimeDays"] = m.LeadTimeDays, ["leadTimeSource"] = m.LeadTimeSource,
                ["planningMethod"] = m.PlanningMethod, ["lotSizing"] = m.LotSizing, ["minOrderQty"] = Num(m.MinOrderQty),
                ["orderMultiple"] = Num(m.OrderMultiple), ["safetyStock"] = Num(m.SafetyStock), ["reorderPoint"] = Num(m.ReorderPoint),
                ["maxStockLevel"] = Num(m.MaxStockLevel), ["fixedLotSize"] = Num(m.FixedLotSize),
                ["fieldsOverriddenInCobalt"] = new JsonArray(m.OverriddenFields.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            };
            o["master"] = md;
        }
        o["masterNote"] = masterNote;
        o["inRun"] = new JsonObject
        {
            ["scope"] = "Only events inside the run horizon. On-hand stock is not stored with a saved run.",
            ["salesOrderDemand"] = EventTotals(events.Where(e => e.Kind == CalendarEventKind.Demand)),
            ["forecastDemand"] = EventTotals(events.Where(e => e.Kind == CalendarEventKind.Forecast)),
            ["openPurchaseOrders"] = EventTotals(events.Where(e => e.Kind == CalendarEventKind.Supply && !e.Label.StartsWith("Production", StringComparison.OrdinalIgnoreCase))),
            ["openProductionOrders"] = EventTotals(events.Where(e => e.Kind == CalendarEventKind.Supply && e.Label.StartsWith("Production", StringComparison.OrdinalIgnoreCase))),
            ["plannedOrders"] = new JsonObject
            {
                ["count"] = orders.Count, ["qty"] = Num(orders.Sum(x => x.Quantity)),
                ["byType"] = Counts(orders, x => x.Type.ToString()),
                ["orders"] = new JsonArray(orders.OrderBy(x => x.DueDate).Take(10).Select(x => (JsonNode)OrderRow(x)).ToArray()),
                ["truncated"] = orders.Count > 10,
            },
            ["exceptions"] = new JsonObject
            {
                ["count"] = exceptions.Count,
                ["items"] = new JsonArray(exceptions.OrderBy(x => Urgency(x.Type)).Take(8).Select(x => (JsonNode)ExceptionRow(x)).ToArray()),
            },
        };
        return o;
    }

    private JsonObject CalendarEvents(JsonObject a)
    {
        var item = Str(a, "item") ?? Str(a, "itemCode");
        var from = _run.HorizonStart; var to = _run.HorizonEnd;
        if (Str(a, "from") is { } f) { if (!DateOnly.TryParse(f, CultureInfo.InvariantCulture, out from)) return Error("from must be a date yyyy-MM-dd."); }
        if (Str(a, "to") is { } t) { if (!DateOnly.TryParse(t, CultureInfo.InvariantCulture, out to)) return Error("to must be a date yyyy-MM-dd."); }
        if (to < from) return Error("from must not be after to.");
        if (to.DayNumber - from.DayNumber > 400) return Error("Ask for at most about a year at a time.");

        var kinds = new HashSet<CalendarEventKind>();
        foreach (var k in (Str(a, "kinds") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<CalendarEventKind>(k, true, out var kind) || kind == CalendarEventKind.NonWorking || !Enum.IsDefined(kind))
                return Error($"Unknown kind '{Trunc(k, 30)}'.");
            kinds.Add(kind);
        }
        var q = _run.Events.Where(e => e.Kind != CalendarEventKind.NonWorking && e.Date >= from && e.Date <= to);
        if (kinds.Count > 0) q = q.Where(e => kinds.Contains(e.Kind));
        if (item is not null) q = q.Where(e => ItemMatch(e.ItemCode, item));
        var all = q.OrderBy(e => e.Date).ThenBy(e => e.Kind).ThenBy(e => e.ItemCode, StringComparer.Ordinal).ToList();
        var limit = Limit(a);
        return new JsonObject
        {
            ["from"] = D(from), ["to"] = D(to), ["total"] = all.Count, ["returned"] = Math.Min(all.Count, limit), ["truncated"] = all.Count > limit,
            ["events"] = new JsonArray(all.Take(limit).Select(e =>
            {
                var r = new JsonObject
                {
                    ["date"] = D(e.Date), ["kind"] = e.Kind.ToString(), ["item"] = e.ItemCode, ["qty"] = Num(e.Quantity),
                    ["label"] = Trunc(e.Label, 120),
                };
                if (e.WarehouseCode is not null) r["warehouse"] = e.WarehouseCode;
                if (e.Severity != "info") r["severity"] = e.Severity;
                return (JsonNode)r;
            }).ToArray()),
        };
    }
}
