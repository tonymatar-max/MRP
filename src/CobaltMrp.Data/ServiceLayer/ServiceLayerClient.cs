using System.Net;
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
        };

        return await PostDocumentAsync("ProductionOrders", doc, ct);
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
                RequiredDate = Date(o.DueDate),
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

    private async Task<int> PostDocumentAsync(string resource, object payload, CancellationToken ct)
    {
        if (!_loggedIn)
            throw new InvalidOperationException("Call LoginAsync before creating documents.");

        using var resp = await _http.PostAsJsonAsync(resource, payload, Json, ct);
        await EnsureSuccessAsync(resp, resource, ct);

        // Service Layer echoes the created entity, including DocEntry.
        var created = await resp.Content.ReadFromJsonAsync<DocEntryResponse>(Json, ct);
        return created?.DocEntry
               ?? throw new ServiceLayerException($"POST {resource} succeeded but returned no DocEntry.");
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
            $"Service Layer {op} failed ({(int)resp.StatusCode} {resp.StatusCode}): {message ?? body}");
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
    }

    private sealed record DocEntryResponse
    {
        public int DocEntry { get; init; }
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
public sealed class ServiceLayerException : Exception
{
    public ServiceLayerException(string message) : base(message) { }
    public ServiceLayerException(string message, Exception inner) : base(message, inner) { }
}
