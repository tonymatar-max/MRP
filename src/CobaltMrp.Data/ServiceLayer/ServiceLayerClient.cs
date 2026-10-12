using System.Net;
using System.Text.RegularExpressions;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CobaltMrp.Domain;

namespace CobaltMrp.Data.ServiceLayer;

/// <summary>Options for connecting to a SAP B1 Service Layer (OData v4) endpoint.</summary>
public sealed class ServiceLayerOptions
{
    /// <summary>Base URL, e.g. <c>https://b1.contoso.local:50000/b1s/v1</c>.</summary>
    public required string BaseUrl { get; init; }
    public required string CompanyDb { get; init; }
    public required string UserName { get; init; }
    public required string Password { get; init; }

    /// <summary>
    /// When true, the HTTPS certificate is NOT validated. Service Layer ships with a self-signed
    /// certificate, so this is a convenience for local/dev only. Defaults to false (strict).
    /// </summary>
    public bool AllowInvalidCertificates { get; init; }
}

/// <summary>
/// A thin <see cref="HttpClient"/>-based client for the SAP B1 Service Layer. It handles the
/// cookie-based <c>B1SESSION</c> login and posts the minimal JSON payloads required to create
/// purchase and production orders, returning the created <c>DocEntry</c>.
/// </summary>
/// <remarks>
/// No network call is made unless the consumer invokes the methods; the class is written to be
/// correct against the Service Layer API shape. Create one <see cref="HttpClientHandler"/> with a
/// cookie container so the session cookie flows automatically on subsequent requests.
/// </remarks>
public sealed class ServiceLayerClient : IDisposable
{
    private readonly ServiceLayerOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private bool _loggedIn;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // The Service Layer is case-sensitive and expects PascalCase (CardCode, DocumentLines...).
        // Web defaults would emit camelCase, so turn the naming policy off. Reads stay case-insensitive.
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Create a client, building an internal <see cref="HttpClient"/> with a cookie container.</summary>
    public ServiceLayerClient(ServiceLayerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
        };

        if (_options.AllowInvalidCertificates)
        {
            // DEV ONLY: trust the Service Layer self-signed certificate. Never enable in production.
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        _http = new HttpClient(handler) { BaseAddress = new Uri(EnsureTrailingSlash(_options.BaseUrl)) };
        _ownsHttp = true;
    }

    /// <summary>Create a client over a caller-supplied <see cref="HttpClient"/> (e.g. from IHttpClientFactory).</summary>
    public ServiceLayerClient(ServiceLayerOptions options, HttpClient http)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _http.BaseAddress ??= new Uri(EnsureTrailingSlash(_options.BaseUrl));
        _ownsHttp = false;
    }

    /// <summary>
    /// Authenticate against <c>/Login</c>. The Service Layer returns a <c>B1SESSION</c> cookie which
    /// the handler's cookie container stores and replays on subsequent requests.
    /// </summary>
    public async Task LoginAsync(CancellationToken ct = default)
    {
        var payload = new LoginRequest(_options.CompanyDb, _options.UserName, _options.Password);
        using var resp = await _http.PostAsJsonAsync("Login", payload, Json, ct);
        await EnsureSuccessAsync(resp, "Login", ct);
        _loggedIn = true;
    }

    /// <summary>End the current Service Layer session (<c>/Logout</c>).</summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        if (!_loggedIn) return;
        using var resp = await _http.PostAsync("Logout", content: null, ct);
        _loggedIn = false;
        resp.Dispose();
    }

    /// <summary>
    /// Create a Purchase Order from a firmed <see cref="PlannedOrder"/> by POSTing to
    /// <c>/PurchaseOrders</c>. Returns the created <c>DocEntry</c>.
    /// </summary>
    /// <remarks>CardCode (supplier) is not part of the planning result; the caller must supply a
    /// vendor. Here we accept it as a parameter so the payload is complete and valid.</remarks>
    public Task<int> CreatePurchaseOrderAsync(PlannedOrder order, string cardCode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        return CreatePurchaseOrderAsync(new[] { order }, cardCode, ct);
    }

    /// <summary>One purchase order for one supplier with a line per planned order. The header due date is the earliest line;
    /// every line keeps its own <c>ShipDate</c>.</summary>
    public async Task<int> CreatePurchaseOrderAsync(IReadOnlyList<PlannedOrder> orders, string cardCode, CancellationToken ct = default)
    {
        RequireOrders(orders);
        var doc = new PurchaseOrderDto
        {
            CardCode = cardCode,
            DocDueDate = Date(orders.Min(o => o.DueDate)),
            Comments = Remark(orders),
        };
        foreach (var o in orders)
            doc.DocumentLines.Add(new DocumentLineDto
            {
                ItemCode = o.ItemCode, Quantity = o.Quantity, WarehouseCode = o.WarehouseCode, ShipDate = Date(o.DueDate),
                UoMEntry = await InventoryUoMEntryAsync(o.ItemCode, ct),
            });
        return await PostDocumentAsync("PurchaseOrders", doc, ct);
    }

    /// <summary>
    /// Create a Production Order from a firmed <see cref="PlannedOrder"/> by POSTing to
    /// <c>/ProductionOrders</c>. Returns the created <c>DocEntry</c>.
    /// </summary>
    public async Task<int> CreateProductionOrderAsync(PlannedOrder order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        var doc = new ProductionOrderDto
        {
            ItemNo = order.ItemCode,
            PlannedQuantity = order.Quantity,
            Warehouse = order.WarehouseCode,
            DueDate = order.DueDate.ToString("yyyy-MM-dd"),
            StartDate = order.ReleaseDate.ToString("yyyy-MM-dd"),
            // SAP only allows creating production orders in planned status (OWOR.Status); releasing is a later step in B1.
            ProductionOrderStatus = "boposPlanned",
            ProductionOrderType = "bopotStandard",
            Remarks = Remark(new[] { order }),
        };

        // The ProductionOrders entity has no DocEntry: SAP answers with AbsoluteEntry (= OWOR.DocEntry, the key used by
        // ProductionOrders(n) and by the OWOR:n document keys in this app).
        return await PostDocumentAsync("ProductionOrders", doc, ct, keyName: "AbsoluteEntry");
    }

    /// <summary>
    /// Create a Purchase Request (<c>/PurchaseRequests</c>). The supplier is optional on a request. Note SAP's own
    /// spelling of the header field: <c>RequriedDate</c>.
    /// </summary>
    public Task<int> CreatePurchaseRequestAsync(PlannedOrder order, string? vendorCode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        return CreatePurchaseRequestAsync(new[] { order }, vendorCode, ct);
    }

    /// <summary>One purchase request with a line per planned order. The header required date is the earliest line; every line
    /// keeps its own <c>RequiredDate</c> (SAP B1 DocumentLine field, checked against the Service Layer metadata).</summary>
    public async Task<int> CreatePurchaseRequestAsync(IReadOnlyList<PlannedOrder> orders, string? vendorCode, CancellationToken ct = default)
    {
        RequireOrders(orders);
        var earliest = Date(orders.Min(o => o.DueDate));
        var doc = new PurchaseRequestDto { RequriedDate = earliest, DocDueDate = earliest, Comments = Remark(orders) };
        foreach (var o in orders)
            doc.DocumentLines.Add(new PurchaseRequestLineDto
            {
                ItemCode = o.ItemCode, Quantity = o.Quantity, WarehouseCode = o.WarehouseCode,
                RequiredDate = Date(o.DueDate), UoMEntry = await InventoryUoMEntryAsync(o.ItemCode, ct),
                LineVendor = string.IsNullOrWhiteSpace(vendorCode) ? null : vendorCode,
            });
        return await PostDocumentAsync("PurchaseRequests", doc, ct);
    }

    /// <summary>Create an inventory transfer request (<c>/InventoryTransferRequests</c>) from the sending to the receiving warehouse.</summary>
    public Task<int> CreateTransferRequestAsync(PlannedOrder order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        return CreateTransferRequestAsync(new[] { order }, ct);
    }

    /// <summary>One transfer request with a line per planned order. All orders must go from the same warehouse to the same warehouse.</summary>
    public async Task<int> CreateTransferRequestAsync(IReadOnlyList<PlannedOrder> orders, CancellationToken ct = default)
    {
        RequireOrders(orders);
        var from = orders[0].FromWarehouse;
        foreach (var o in orders)
        {
            if (string.IsNullOrWhiteSpace(o.FromWarehouse))
                throw new InvalidOperationException($"Transfer for item {o.ItemCode} has no sending warehouse.");
            if (!string.Equals(o.FromWarehouse, from, StringComparison.OrdinalIgnoreCase) || !string.Equals(o.WarehouseCode, orders[0].WarehouseCode, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Transfers on one request must share the same sending and receiving warehouse.");
        }
        var doc = new TransferRequestDto { FromWarehouse = from, ToWarehouse = orders[0].WarehouseCode, DueDate = Date(orders.Min(o => o.DueDate)), Comments = Remark(orders) };
        foreach (var o in orders)
            doc.StockTransferLines.Add(new TransferRequestLineDto
            {
                ItemCode = o.ItemCode, Quantity = o.Quantity, FromWarehouseCode = o.FromWarehouse, WarehouseCode = o.WarehouseCode,
                UoMEntry = await InventoryUoMEntryAsync(o.ItemCode, ct),
            });
        return await PostDocumentAsync("InventoryTransferRequests", doc, ct);
    }

    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd");

    private static void RequireOrders(IReadOnlyList<PlannedOrder> orders)
    {
        ArgumentNullException.ThrowIfNull(orders);
        if (orders.Count == 0) throw new ArgumentException("At least one planned order is needed.", nameof(orders));
    }

    /// <summary>SAP B1 allows 254 characters in a document's remarks: one order names itself, several list short ids.</summary>
    public static string Remark(IReadOnlyList<PlannedOrder> orders)
    {
        if (orders.Count == 1) return $"Cobalt MRP planned order {orders[0].Id}";
        var head = $"Cobalt MRP planned orders ({orders.Count}): ";
        var text = head;
        var shown = 0;
        foreach (var o in orders)
        {
            var id = o.Id.ToString("N")[..8];
            var next = shown == 0 ? id : ", " + id;
            if (text.Length + next.Length + 12 > 254) break;
            text += next; shown++;
        }
        return shown < orders.Count ? $"{text} +{orders.Count - shown} more" : text;
    }

    // ---- Reading and changing existing documents --------------------------------------------------------------

    /// <summary>GET an entity, e.g. <c>PurchaseOrders(4501)</c>. Read-only.</summary>
    public async Task<JsonElement> GetEntityAsync(string path, CancellationToken ct = default)
    {
        if (!_loggedIn) throw new InvalidOperationException("Call LoginAsync before reading documents.");
        using var resp = await _http.GetAsync(path, ct);
        await EnsureSuccessAsync(resp, path, ct);
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    /// <summary>PATCH an entity. Collections are merged by their key (e.g. DocumentLines by LineNum), never replaced.</summary>
    public async Task PatchEntityAsync(string path, object body, CancellationToken ct = default)
    {
        if (!_loggedIn) throw new InvalidOperationException("Call LoginAsync before changing documents.");
        using var req = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = JsonContent.Create(body, options: Json),
        };
        // Explicit: update only the lines we name; leave every other line untouched.
        req.Headers.TryAddWithoutValidation("B1S-ReplaceCollectionsOnPatch", "false");
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, $"PATCH {path}", ct);
    }

    /// <summary>POST a bound action such as <c>PurchaseOrders(4501)/Close</c>.</summary>
    public async Task PostActionAsync(string path, CancellationToken ct = default)
    {
        if (!_loggedIn) throw new InvalidOperationException("Call LoginAsync before changing documents.");
        using var resp = await _http.PostAsync(path, content: null, ct);
        await EnsureSuccessAsync(resp, $"POST {path}", ct);
    }

    private async Task<int> PostDocumentAsync(string resource, object payload, CancellationToken ct, string keyName = "DocEntry")
    {
        if (!_loggedIn)
            throw new InvalidOperationException("Call LoginAsync before creating documents.");

        using var resp = await _http.PostAsJsonAsync(resource, payload, Json, ct);
        await EnsureSuccessAsync(resp, resource, ct);

        // The Service Layer echoes the created entity. A missing or 0 key is NEVER a success: the document may well exist
        // in SAP B1, so this is an unknown outcome (the caller looks for it by its remark instead of re-posting).
        long key = 0;
        try
        {
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(keyName, out var v) && v.ValueKind == JsonValueKind.Number)
                key = v.GetInt64();
        }
        catch (JsonException) { /* handled below */ }
        if (key <= 0 || key > int.MaxValue)
            throw new ServiceLayerOutcomeUnknownException($"POST {resource} was accepted by SAP B1 but its answer had no valid {keyName}; the document may exist in SAP B1.");
        return (int)key;
    }

    // ---- Item units of measure ------------------------------------------------------------------------------------

    private readonly Dictionary<string, int?> _uomCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The inventory UoM entry to put on a document line, or null when the item has no UoM group (SAP B1 then
    /// needs no UoM). Planned quantities are in the inventory unit, so that is the unit sent. Read once per item per client
    /// (= per release). A failed lookup yields null: the document is then posted as before and SAP B1 gives the exact reason.</summary>
    public async Task<int?> InventoryUoMEntryAsync(string itemCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemCode)) return null;
        if (_uomCache.TryGetValue(itemCode, out var cached)) return cached;
        int? result = null;
        try
        {
            var item = await GetEntityAsync($"Items('{Uri.EscapeDataString(itemCode.Replace("'", "''"))}')?$select=UoMGroupEntry,InventoryUoMEntry", ct);
            var group = item.TryGetProperty("UoMGroupEntry", out var g) && g.ValueKind == JsonValueKind.Number ? g.GetInt32() : -1;
            var inv = item.TryGetProperty("InventoryUoMEntry", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
            if (group != -1 && inv > 0) result = inv;
        }
        catch (ServiceLayerException) { /* unknown item or no access: do not guess */ }
        _uomCache[itemCode] = result;
        return result;
    }

    // ---- Finding documents this app already created (idempotency) ----------------------------------------------------

    /// <summary>Which of these planned orders already have a live (not cancelled) document in SAP B1, found by the planned-order
    /// tag in the document remarks and confirmed against the lines (item, quantity). Used before posting and after a
    /// connection error, so a lost answer never produces a duplicate. Returns planned order id to document key.</summary>
    public async Task<Dictionary<Guid, int>> FindExistingAsync(IReadOnlyList<PlannedOrder> orders, CancellationToken ct = default)
    {
        var found = new Dictionary<Guid, int>();
        if (orders.Count == 0) return found;
        var type = orders[0].Type;
        var (resource, keyName, remarkField, extra) = type switch
        {
            PlannedOrderType.PurchaseOrder => ("PurchaseOrders", "DocEntry", "Comments", " and Cancelled eq 'tNO'"),
            PlannedOrderType.PurchaseRequest => ("PurchaseRequests", "DocEntry", "Comments", " and Cancelled eq 'tNO'"),
            PlannedOrderType.TransferRequest => ("InventoryTransferRequests", "DocEntry", "Comments", " and DocumentStatus eq 'bost_Open'"),
            _ => ("ProductionOrders", "AbsoluteEntry", "Remarks", " and ProductionOrderStatus ne 'boposCancelled'"),
        };
        var filter = Uri.EscapeDataString($"contains({remarkField},'Cobalt MRP planned order'){extra}");
        var path = $"{resource}?$select={keyName},{remarkField}&$filter={filter}&$orderby={keyName}%20desc";
        var list = await GetListAsync(path, ct);
        foreach (var head in list)
        {
            var remark = Str(head, remarkField);
            if (remark is null || !head.TryGetProperty(keyName, out var k) || k.ValueKind != JsonValueKind.Number) continue;
            var key = k.GetInt32();
            var candidates = orders.Where(o => !found.ContainsKey(o.Id) && remark.Contains(o.Id.ToString("N")[..8], StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0) continue;
            var full = await GetEntityAsync($"{resource}({key})", ct);
            foreach (var o in candidates)
                if (LineMatches(full, type, o)) found[o.Id] = key;
        }
        return found;
    }

    private static bool LineMatches(JsonElement doc, PlannedOrderType type, PlannedOrder o)
    {
        if (type == PlannedOrderType.ProductionOrder)
            return string.Equals(Str(doc, "ItemNo"), o.ItemCode, StringComparison.OrdinalIgnoreCase)
                && doc.TryGetProperty("PlannedQuantity", out var pq) && pq.ValueKind == JsonValueKind.Number && Math.Abs(pq.GetDecimal() - o.Quantity) < 0.0001m;
        var coll = type == PlannedOrderType.TransferRequest ? "StockTransferLines" : "DocumentLines";
        if (!doc.TryGetProperty(coll, out var lines) || lines.ValueKind != JsonValueKind.Array) return false;
        foreach (var l in lines.EnumerateArray())
            if (string.Equals(Str(l, "ItemCode"), o.ItemCode, StringComparison.OrdinalIgnoreCase)
                && l.TryGetProperty("Quantity", out var q) && q.ValueKind == JsonValueKind.Number && Math.Abs(q.GetDecimal() - o.Quantity) < 0.0001m)
                return true;
        return false;
    }

    private static string? Str(JsonElement j, string name) =>
        j.ValueKind == JsonValueKind.Object && j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>GET a collection, first answer only (the newest 100 rows are enough to find a just-created document).</summary>
    private async Task<List<JsonElement>> GetListAsync(string path, CancellationToken ct)
    {
        if (!_loggedIn) throw new InvalidOperationException("Call LoginAsync before reading documents.");
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.TryAddWithoutValidation("Prefer", "odata.maxpagesize=100");
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, path, ct);
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.Clone()).ToList() : new List<JsonElement>();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, string op, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        string? message = null;
        try
        {
            var err = JsonSerializer.Deserialize<ODataError>(body, Json);
            message = err?.Error?.Message?.Value;
        }
        catch (JsonException) { /* non-JSON error body; fall back to raw text */ }

        throw new ServiceLayerException(
            $"Service Layer {op} failed ({(int)resp.StatusCode} {resp.StatusCode}): {message ?? ReasonFromBody(body, resp)}");
    }

    /// <summary>An error body that is not SAP's JSON (typically an HTML page from a proxy or the wrong path) is reduced to one line.</summary>
    internal static string ReasonFromBody(string? body, HttpResponseMessage resp)
    {
        var text = (body ?? "").Trim();
        if (text.Length == 0) return resp.ReasonPhrase ?? "no details";
        if (text.StartsWith('<'))
        {
            var title = Regex.Match(text, "<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var line = title.Success ? Regex.Replace(title.Groups[1].Value, @"\s+", " ").Trim() : "";
            if (line.Length == 0) line = resp.ReasonPhrase ?? "error";
            return $"{line} (the server answered with a web page, not a Service Layer message: check the base URL)";
        }
        text = Regex.Replace(text, @"\s+", " ");
        return text.Length > 300 ? text[..300] + "..." : text;
    }

    private static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    // ---- JSON contracts (minimal Service Layer shapes) --------------------------------------

    private sealed record LoginRequest(
        [property: JsonPropertyName("CompanyDB")] string CompanyDb,
        [property: JsonPropertyName("UserName")] string UserName,
        [property: JsonPropertyName("Password")] string Password);

    private sealed class PurchaseRequestDto
    {
        public string? RequriedDate { get; set; }
        public string? DocDueDate { get; set; }
        public string? Comments { get; set; }
        public List<PurchaseRequestLineDto> DocumentLines { get; set; } = new();
    }

    private sealed class PurchaseRequestLineDto
    {
        public string? ItemCode { get; set; }
        public decimal Quantity { get; set; }
        public string? WarehouseCode { get; set; }
        public string? RequiredDate { get; set; }
        public string? LineVendor { get; set; }
        public int? UoMEntry { get; set; }
    }

    private sealed class TransferRequestDto
    {
        public string? FromWarehouse { get; set; }
        public string? ToWarehouse { get; set; }
        public string? DueDate { get; set; }
        public string? Comments { get; set; }
        public List<TransferRequestLineDto> StockTransferLines { get; set; } = new();
    }

    private sealed class TransferRequestLineDto
    {
        public string? ItemCode { get; set; }
        public decimal Quantity { get; set; }
        public string? FromWarehouseCode { get; set; }
        public string? WarehouseCode { get; set; }
        public int? UoMEntry { get; set; }
    }

    private sealed class PurchaseOrderDto
    {
        public string? CardCode { get; set; }
        public string? DocDueDate { get; set; }
        public string? Comments { get; set; }
        public List<DocumentLineDto> DocumentLines { get; set; } = new();
    }

    private sealed class DocumentLineDto
    {
        public string? ItemCode { get; set; }
        public decimal Quantity { get; set; }
        public string? WarehouseCode { get; set; }
        public string? ShipDate { get; set; }
        public int? UoMEntry { get; set; }
    }

    private sealed class ProductionOrderDto
    {
        public string? ItemNo { get; set; }
        public decimal PlannedQuantity { get; set; }
        public string? Warehouse { get; set; }
        public string? DueDate { get; set; }
        public string? StartDate { get; set; }
        public string? ProductionOrderStatus { get; set; }
        public string? ProductionOrderType { get; set; }
        public string? Remarks { get; set; }
    }

    private sealed record ODataError
    {
        [JsonPropertyName("error")] public ODataErrorBody? Error { get; init; }
    }

    private sealed record ODataErrorBody
    {
        [JsonPropertyName("code")] public JsonElement? Code { get; init; }  // B1 returns a number (e.g. -5002)
        [JsonPropertyName("message")] public ODataErrorMessage? Message { get; init; }
    }

    private sealed record ODataErrorMessage
    {
        [JsonPropertyName("lang")] public string? Lang { get; init; }
        [JsonPropertyName("value")] public string? Value { get; init; }
    }
}

/// <summary>Raised when a Service Layer request fails.</summary>
public class ServiceLayerException : Exception
{
    public ServiceLayerException(string message) : base(message) { }
    public ServiceLayerException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>SAP B1 accepted the request but Cobalt could not read the new document's key: the document may exist in SAP B1.
/// Must be caught before <see cref="ServiceLayerException"/>.</summary>
public sealed class ServiceLayerOutcomeUnknownException : ServiceLayerException
{
    public ServiceLayerOutcomeUnknownException(string message) : base(message) { }
}
