using System.Net;
using System.Text;
using System.Text.Json;
using CobaltMrp.Data.ServiceLayer;
using CobaltMrp.Domain;
using Xunit;

namespace CobaltMrp.Data.Tests;

/// <summary>
/// QA round 1 defects against a real SAP B1: production-order keys, the bad line inside a combined document, connection errors
/// (idempotent retry), units of measure and HTML error bodies.
/// </summary>
public class WritebackResilienceTests
{
    private sealed class Fake : HttpMessageHandler
    {
        public List<(string Path, string Body)> Posts { get; } = new();
        public List<string> Gets { get; } = new();
        public Func<string, string, bool> Refuse { get; set; } = (_, _) => false;
        public Func<string, string>? Get { get; set; }
        public Func<int, bool>? Drop { get; set; }
        public bool CreateBeforeDrop { get; set; }
        public int Created { get; private set; }
        public string PostReply { get; set; } = "";     // overrides the normal answer when set
        private int _next = 1000;
        private int _postNo;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("Login")) return Json(HttpStatusCode.OK, "{}");
            if (req.Method == HttpMethod.Get)
            {
                var uri = Uri.UnescapeDataString(req.RequestUri!.PathAndQuery);
                Gets.Add(uri);
                return Json(HttpStatusCode.OK, Get?.Invoke(uri) ?? "{\"value\":[]}");
            }
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            Posts.Add((path.Split('/').Last(), body));
            _postNo++;
            if (Drop?.Invoke(_postNo) == true)
            {
                if (CreateBeforeDrop) Created++;
                throw new HttpRequestException("The response ended prematurely.");
            }
            if (Refuse(path, body))
                return Json(HttpStatusCode.BadRequest, "{\"error\":{\"code\":-5002,\"message\":{\"lang\":\"en-us\",\"value\":\"refused\"}}}");
            if (PostReply.Length > 0) return Json(HttpStatusCode.Created, PostReply);
            return Json(HttpStatusCode.Created, $"{{\"DocEntry\":{_next++},\"AbsoluteEntry\":{_next - 1}}}");
        }

        private static HttpResponseMessage Json(HttpStatusCode c, string t) => new(c) { Content = new StringContent(t, Encoding.UTF8, "application/json") };
    }

    private static async Task<(PlannedOrderWriteback Wb, Fake Fake)> Make()
    {
        var fake = new Fake();
        var http = new HttpClient(fake) { BaseAddress = new Uri("http://sl/b1s/v1/") };
        var client = new ServiceLayerClient(new ServiceLayerOptions { BaseUrl = "http://sl/b1s/v1", CompanyDb = "DB", UserName = "u", Password = "p" }, http);
        await client.LoginAsync();
        return (new PlannedOrderWriteback(client, o => o.VendorCode), fake);
    }

    private static PlannedOrder Po(string item, string? vendor, string due = "2026-11-10", PlannedOrderType type = PlannedOrderType.PurchaseOrder,
        string wh = "WH01", string? from = null) => new()
    {
        ItemCode = item, WarehouseCode = wh, FromWarehouse = from, Type = type, Quantity = 5, Firmed = true, VendorCode = vendor,
        DueDate = DateOnly.Parse(due), ReleaseDate = DateOnly.Parse(due).AddDays(-7),
    };

    private static JsonElement Doc(string body) => JsonDocument.Parse(body).RootElement;

    // ---- D1: production order keys ----------------------------------------------------------------------------------

    [Fact]
    public async Task ProductionOrder_UsesAbsoluteEntry_AsTheDocumentKey()
    {
        var (wb, fake) = await Make();
        fake.PostReply = "{\"AbsoluteEntry\":217,\"DocumentNumber\":300}";            // what the real Service Layer answers
        var o = Po("OIL", null, type: PlannedOrderType.ProductionOrder);
        var s = await wb.PushAsync(new[] { o }, default, null, DocumentGrouping.Supplier);
        var r = Assert.Single(s.Results);
        Assert.True(r.Success); Assert.Equal(217, r.DocEntry); Assert.Equal("217", o.B1DocEntry);
    }

    [Theory]
    [InlineData("{\"DocumentNumber\":216}")]
    [InlineData("{\"AbsoluteEntry\":0}")]
    [InlineData("{}")]
    public async Task ProductionOrder_WithoutAKey_IsNotASuccess_AndIsNotRememberedAsPushed(string reply)
    {
        var (wb, fake) = await Make();
        fake.PostReply = reply;
        var o = Po("OIL", null, type: PlannedOrderType.ProductionOrder);
        var s = await wb.PushAsync(new[] { o }, default, null, DocumentGrouping.None);
        var f = Assert.Single(s.Failed);
        Assert.Contains("AbsoluteEntry", f.Error);
        Assert.Null(o.B1DocEntry);                                  // a retry must not say "Already pushed as DocEntry 0"
    }

    // ---- D2: bisecting a refused combined document ------------------------------------------------------------------

    [Fact]
    public async Task OneBadLineIn100_KeepsTheOtherLinesCombined()
    {
        var (wb, fake) = await Make();
        fake.Refuse = (_, body) => body.Contains("\"ItemCode\":\"I37\"");
        var orders = Enumerable.Range(0, 100).Select(i => Po($"I{i}", "V1")).ToArray();
        var s = await wb.PushAsync(orders, default, null, DocumentGrouping.Supplier);

        Assert.Equal(99, s.Succeeded.Count());
        var bad = Assert.Single(s.Failed);
        Assert.Equal(orders[37].Id, bad.PlannedOrderId);
        Assert.Equal("refused", bad.Error!.Split(": ").Last());       // SAP's single-line message is kept
        Assert.InRange(s.Succeeded.Select(r => r.DocEntry).Distinct().Count(), 2, 12);   // a handful of documents, not 99 single-line ones
        Assert.True(fake.Posts.Count < 30, fake.Posts.Count.ToString());
    }

    [Fact]
    public async Task TwoBadLines_AreBothIsolated_TheRestStaysCombined()
    {
        var (wb, fake) = await Make();
        fake.Refuse = (_, body) => body.Contains("\"ItemCode\":\"T0001\"") || body.Contains("\"ItemCode\":\"X2\"");
        var orders = new[] { Po("A", "V1"), Po("T0001", "V1"), Po("B", "V1"), Po("C", "V1"), Po("X2", "V1"), Po("D", "V1") };
        var s = await wb.PushAsync(orders, default, null, DocumentGrouping.Supplier);
        Assert.Equal(4, s.Succeeded.Count());
        Assert.Equal(new[] { orders[1].Id, orders[4].Id }.OrderBy(x => x), s.Failed.Select(f => f.PlannedOrderId).OrderBy(x => x));
        Assert.True(s.Succeeded.Select(r => r.DocEntry).Distinct().Count() <= 4);
    }

    // ---- D4: connection errors ----------------------------------------------------------------------------------------

    [Fact]
    public async Task DroppedConnection_OnOneGroup_DoesNotAbortTheOthers()
    {
        var (wb, fake) = await Make();
        fake.Drop = n => n == 2;                                      // the second document's POST dies
        var a = Po("A", "V1"); var b = Po("B", "V2"); var c = Po("C", "V3");
        var s = await wb.PushAsync(new[] { a, b, c }, default, null, DocumentGrouping.Supplier);

        Assert.Equal(2, s.Succeeded.Count());
        var failed = Assert.Single(s.Failed);
        Assert.Equal(b.Id, failed.PlannedOrderId);
        Assert.Contains("connection", failed.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(b.B1DocEntry);
        Assert.NotNull(c.B1DocEntry);                                 // the group after the failure was still attempted
    }

    [Fact]
    public async Task LostAnswer_ThenSapHasTheDocument_IsAdoptedNotDuplicated()
    {
        var (wb, fake) = await Make();
        var a = Po("A", "V1"); var b = Po("B", "V1");
        fake.Drop = n => n == 1; fake.CreateBeforeDrop = true;
        var remark = ServiceLayerClient.Remark(new[] { a, b });
        // The first search (before posting) finds nothing; after the drop the search finds the document the fake SAP created.
        fake.Get = uri => uri.StartsWith("/b1s/v1/PurchaseOrders?") && fake.Created > 0
            ? "{\"value\":[{\"DocEntry\":777,\"Comments\":\"" + remark + "\"}]}"
            : uri.Contains("PurchaseOrders(777)")
                ? "{\"DocEntry\":777,\"DocumentLines\":[{\"ItemCode\":\"A\",\"Quantity\":5},{\"ItemCode\":\"B\",\"Quantity\":5}]}"
                : "{\"value\":[]}";
        var s = await wb.PushAsync(new[] { a, b }, default, null, DocumentGrouping.Supplier);

        Assert.All(s.Results, r => { Assert.True(r.Success); Assert.True(r.Adopted); Assert.Equal(777, r.DocEntry); });
        Assert.Equal("777", a.B1DocEntry);
        Assert.Single(fake.Posts);                                    // no second POST
    }

    [Fact]
    public async Task LostAnswer_AndSapHasNoDocument_SaysNotCreated()
    {
        var (wb, fake) = await Make();
        fake.Drop = _ => true;
        var s = await wb.PushAsync(new[] { Po("A", "V1") }, default, null, DocumentGrouping.Supplier);
        var f = Assert.Single(s.Failed);
        Assert.False(f.OutcomeUnknown);                               // the search worked and found nothing
        Assert.Contains("no document exists", f.Error);
    }

    [Fact]
    public async Task LostAnswer_AndSapCannotBeChecked_IsMarkedOutcomeUnknown()
    {
        var (wb, fake) = await Make();
        fake.Drop = _ => true;
        fake.Get = uri => uri.Contains("PurchaseOrders?") ? throw new HttpRequestException("down") : "{\"value\":[]}";
        var s = await wb.PushAsync(new[] { Po("A", "V1") }, default, null, DocumentGrouping.Supplier);
        var f = Assert.Single(s.Failed);
        Assert.True(f.OutcomeUnknown);
        Assert.Contains("UNKNOWN", f.Error);
        Assert.Contains("connection", f.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retry_FirstLooksForAnExistingDocument_AndAdoptsIt()
    {
        var (wb, fake) = await Make();
        var a = Po("A", "V1");
        fake.Get = uri => uri.StartsWith("/b1s/v1/PurchaseOrders?")
            ? "{\"value\":[{\"DocEntry\":555,\"Comments\":\"" + ServiceLayerClient.Remark(new[] { a }) + "\"}]}"
            : uri.Contains("PurchaseOrders(555)") ? "{\"DocEntry\":555,\"DocumentLines\":[{\"ItemCode\":\"A\",\"Quantity\":5}]}" : "{\"value\":[]}";
        var s = await wb.PushAsync(new[] { a }, default, null, DocumentGrouping.None);
        Assert.Empty(fake.Posts);
        var r = Assert.Single(s.Results);
        Assert.True(r.Success && r.Adopted); Assert.Equal(555, r.DocEntry); Assert.Equal("555", a.B1DocEntry);
    }

    [Fact]
    public async Task ADocumentWithTheSameTagButOtherLines_IsNotAdopted()
    {
        var (wb, fake) = await Make();
        var a = Po("A", "V1");
        fake.Get = uri => uri.StartsWith("/b1s/v1/PurchaseOrders?")
            ? "{\"value\":[{\"DocEntry\":555,\"Comments\":\"" + ServiceLayerClient.Remark(new[] { a }) + "\"}]}"
            : uri.Contains("PurchaseOrders(555)") ? "{\"DocEntry\":555,\"DocumentLines\":[{\"ItemCode\":\"OTHER\",\"Quantity\":5}]}" : "{\"value\":[]}";
        var s = await wb.PushAsync(new[] { a }, default, null, DocumentGrouping.None);
        Assert.Single(fake.Posts);
        Assert.False(s.Results.Single().Adopted);
    }

    [Fact]
    public async Task ThreeConnectionFailuresInARow_StopTheRelease_TheRestIsReportedNotSent()
    {
        var (wb, fake) = await Make();
        fake.Drop = _ => true;
        var orders = Enumerable.Range(0, 6).Select(i => Po("I" + i, "V" + i)).ToArray();
        var s = await wb.PushAsync(orders, default, null, DocumentGrouping.Supplier);
        Assert.Equal(6, s.Failed.Count());
        Assert.Equal(3, fake.Posts.Count);
        Assert.Equal(3, s.Failed.Count(f => f.Error!.StartsWith("Not sent")));
    }

    [Fact]
    public async Task TimeoutIsATransportError_NotACancellation()
    {
        var (wb, fake) = await Make();
        fake.Drop = _ => throw new TaskCanceledException("timeout");
        var s = await wb.PushAsync(new[] { Po("A", "V1"), Po("B", "V2") }, default, null, DocumentGrouping.Supplier);
        Assert.Equal(2, s.Failed.Count());
        Assert.All(s.Failed, f => Assert.Contains("timed out", f.Error));
    }

    // ---- D3: units of measure ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ItemWithUomGroup_GetsTheInventoryUoMEntry_OnOrderRequestAndTransferLines()
    {
        var (wb, fake) = await Make();
        fake.Get = uri => uri.Contains("Items('T0001')") ? "{\"UoMGroupEntry\":4,\"InventoryUoMEntry\":9}"
                        : uri.Contains("Items(") ? "{\"UoMGroupEntry\":-1,\"InventoryUoMEntry\":1}" : "{\"value\":[]}";
        await wb.PushAsync(new[]
        {
            Po("T0001", "V1"), Po("PLAIN", "V1"),
            Po("T0001", null, "2026-11-10", PlannedOrderType.PurchaseRequest),
            Po("T0001", null, "2026-11-10", PlannedOrderType.TransferRequest, "WH02", "WH01"),
        }, default, null, DocumentGrouping.Supplier);

        var po = Doc(fake.Posts.Single(p => p.Path == "PurchaseOrders").Body).GetProperty("DocumentLines");
        Assert.Equal(9, po[0].GetProperty("UoMEntry").GetInt32());
        Assert.False(po[1].TryGetProperty("UoMEntry", out _));        // no UoM group: nothing sent
        Assert.Equal(9, Doc(fake.Posts.Single(p => p.Path == "PurchaseRequests").Body).GetProperty("DocumentLines")[0].GetProperty("UoMEntry").GetInt32());
        Assert.Equal(9, Doc(fake.Posts.Single(p => p.Path == "InventoryTransferRequests").Body).GetProperty("StockTransferLines")[0].GetProperty("UoMEntry").GetInt32());
    }

    [Fact]
    public async Task UomLookup_IsCachedPerItem()
    {
        var (wb, fake) = await Make();
        fake.Get = uri => uri.Contains("Items(") ? "{\"UoMGroupEntry\":4,\"InventoryUoMEntry\":9}" : "{\"value\":[]}";
        await wb.PushAsync(new[] { Po("T0001", "V1"), Po("T0001", "V1", "2026-12-01", PlannedOrderType.PurchaseRequest) }, default, null, DocumentGrouping.None);
        Assert.Equal(1, fake.Gets.Count(g => g.Contains("Items('T0001')")));
    }

    [Fact]
    public async Task UomLookupFailure_StillPostsTheDocument()
    {
        var (wb, fake) = await Make();
        fake.Get = uri => uri.Contains("Items(") ? throw new HttpRequestException("nope") : "{\"value\":[]}";
        // A transport failure on the item lookup is a connection problem and is reported like one (nothing is guessed).
        var s = await wb.PushAsync(new[] { Po("T0001", "V1") }, default, null, DocumentGrouping.None);
        Assert.Single(s.Results);
    }
}
