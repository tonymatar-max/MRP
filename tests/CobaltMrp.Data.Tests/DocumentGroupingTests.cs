using System.Net;
using System.Text;
using System.Text.Json;
using CobaltMrp.Data.ServiceLayer;
using CobaltMrp.Domain;
using Xunit;

namespace CobaltMrp.Data.Tests;

/// <summary>Several planned orders becoming one SAP B1 document, and the fallback when SAP B1 refuses a combined one.</summary>
public class DocumentGroupingTests
{
    private sealed class Fake : HttpMessageHandler
    {
        public List<(string Path, string Body)> Posts { get; } = new();
        public Func<string, string, bool> Refuse { get; set; } = (_, _) => false;
        private int _next = 1000;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("Login")) return Json(HttpStatusCode.OK, "{}");
            if (req.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, "{\"value\":[]}");   // item data / search for existing documents
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            Posts.Add((path.Split('/').Last(), body));
            if (Refuse(path, body))
                return Json(HttpStatusCode.BadRequest, "{\"error\":{\"code\":-5002,\"message\":{\"lang\":\"en-us\",\"value\":\"refused\"}}}");
            return Json(HttpStatusCode.Created, $"{{\"DocEntry\":{_next++},\"AbsoluteEntry\":{_next - 1}}}");
        }

        private static HttpResponseMessage Json(HttpStatusCode c, string t) => new(c) { Content = new StringContent(t, Encoding.UTF8, "application/json") };
    }

    private static async Task<(PlannedOrderWriteback Wb, Fake Fake)> Make(Func<PlannedOrder, string?>? vendor = null)
    {
        var fake = new Fake();
        var http = new HttpClient(fake) { BaseAddress = new Uri("http://sl/b1s/v1/") };
        var client = new ServiceLayerClient(new ServiceLayerOptions { BaseUrl = "http://sl/b1s/v1", CompanyDb = "DB", UserName = "u", Password = "p" }, http);
        await client.LoginAsync();
        return (new PlannedOrderWriteback(client, vendor ?? (o => o.VendorCode)), fake);
    }

    private static PlannedOrder Po(string item, string? vendor, string due = "2026-11-10", PlannedOrderType type = PlannedOrderType.PurchaseOrder,
        string wh = "WH01", string? from = null) => new()
    {
        ItemCode = item, WarehouseCode = wh, FromWarehouse = from, Type = type, Quantity = 5, Firmed = true, VendorCode = vendor,
        DueDate = DateOnly.Parse(due), ReleaseDate = DateOnly.Parse(due).AddDays(-7),
    };

    private static JsonElement Doc(string body) => JsonDocument.Parse(body).RootElement;

    [Fact]
    public async Task SameSupplier_BecomesOnePurchaseOrder_WithALinePerOrder()
    {
        var (wb, fake) = await Make();
        var a = Po("A", "V1", "2026-11-10"); var b = Po("B", "V1", "2026-11-20");
        var s = await wb.PushAsync(new[] { a, b }, default, null, DocumentGrouping.Supplier);

        var post = Assert.Single(fake.Posts);
        Assert.Equal("PurchaseOrders", post.Path);
        var d = Doc(post.Body);
        Assert.Equal("V1", d.GetProperty("CardCode").GetString());
        Assert.Equal("2026-11-10", d.GetProperty("DocDueDate").GetString());           // earliest line
        var lines = d.GetProperty("DocumentLines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal("2026-11-20", lines[1].GetProperty("ShipDate").GetString());       // each line keeps its own date
        Assert.All(s.Results, r => { Assert.True(r.Success); Assert.Equal(2, r.DocLines); Assert.Equal(1000, r.DocEntry); });
        Assert.Equal("1000", a.B1DocEntry); Assert.Equal("1000", b.B1DocEntry);
    }

    [Fact]
    public async Task DifferentSuppliers_GetTheirOwnDocument()
    {
        var (wb, fake) = await Make();
        await wb.PushAsync(new[] { Po("A", "V1"), Po("B", "V2"), Po("C", "V1") }, default, null, DocumentGrouping.Supplier);
        Assert.Equal(2, fake.Posts.Count);
        Assert.Equal(new[] { 2, 1 }, fake.Posts.Select(p => Doc(p.Body).GetProperty("DocumentLines").GetArrayLength()));
    }

    [Fact]
    public async Task NoGrouping_KeepsOneDocumentPerOrder()
    {
        var (wb, fake) = await Make();
        var s = await wb.PushAsync(new[] { Po("A", "V1"), Po("B", "V1") }, default, null, DocumentGrouping.None);
        Assert.Equal(2, fake.Posts.Count);
        Assert.All(s.Results, r => Assert.Equal(1, r.DocLines));
    }

    [Fact]
    public async Task SupplierAndDate_SplitsDifferentDueDates()
    {
        var (wb, fake) = await Make();
        await wb.PushAsync(new[] { Po("A", "V1", "2026-11-10"), Po("B", "V1", "2026-11-20"), Po("C", "V1", "2026-11-10") }, default, null, DocumentGrouping.SupplierAndDate);
        Assert.Equal(2, fake.Posts.Count);
    }

    [Fact]
    public async Task PurchaseRequests_Combine_AndCarryALineRequiredDate()
    {
        var (wb, fake) = await Make();
        await wb.PushAsync(new[] { Po("A", null, "2026-11-10", PlannedOrderType.PurchaseRequest), Po("B", null, "2026-11-12", PlannedOrderType.PurchaseRequest) },
            default, null, DocumentGrouping.Supplier);
        var post = Assert.Single(fake.Posts);
        Assert.Equal("PurchaseRequests", post.Path);
        var d = Doc(post.Body);
        Assert.Equal("2026-11-10", d.GetProperty("RequriedDate").GetString());
        Assert.Equal("2026-11-12", d.GetProperty("DocumentLines")[1].GetProperty("RequiredDate").GetString());
    }

    [Fact]
    public async Task Transfers_Combine_OnlyForTheSameWarehousesAndDate()
    {
        var (wb, fake) = await Make();
        var t = PlannedOrderType.TransferRequest;
        await wb.PushAsync(new[]
        {
            Po("A", null, "2026-11-10", t, "WH02", "WH01"), Po("B", null, "2026-11-10", t, "WH02", "WH01"),
            Po("C", null, "2026-11-11", t, "WH02", "WH01"), Po("D", null, "2026-11-10", t, "WH03", "WH01"),
        }, default, null, DocumentGrouping.Supplier);
        Assert.Equal(3, fake.Posts.Count);
        Assert.Equal(2, Doc(fake.Posts[0].Body).GetProperty("StockTransferLines").GetArrayLength());
    }

    [Fact]
    public async Task ProductionOrders_AreNeverCombined()
    {
        var (wb, fake) = await Make();
        await wb.PushAsync(new[] { Po("A", null, type: PlannedOrderType.ProductionOrder), Po("B", null, type: PlannedOrderType.ProductionOrder) },
            default, null, DocumentGrouping.SupplierAndDate);
        Assert.Equal(2, fake.Posts.Count);
    }

    [Fact]
    public async Task PurchaseOrderWithoutSupplier_IsSentAloneAndFailsOnItsOwn()
    {
        var (wb, fake) = await Make();
        var s = await wb.PushAsync(new[] { Po("A", "V1"), Po("B", null), Po("C", "V1") }, default, null, DocumentGrouping.Supplier);
        Assert.Equal(1, s.Results.Count(r => !r.Success));
        Assert.Contains("No vendor", s.Failed.Single().Error);
        Assert.Equal(2, s.Succeeded.Count());
        Assert.Single(fake.Posts);                                  // A and C went together; B never reached SAP B1
    }

    [Fact]
    public async Task RefusedCombinedDocument_FallsBackToOneByOne_SoOnlyTheBadLineFails()
    {
        var (wb, fake) = await Make();
        fake.Refuse = (_, body) => body.Contains("\"ItemCode\":\"BAD\"");
        var s = await wb.PushAsync(new[] { Po("A", "V1"), Po("BAD", "V1"), Po("C", "V1") }, default, null, DocumentGrouping.Supplier);

        Assert.Equal(5, fake.Posts.Count);                           // bisect: [A,BAD,C] no, [A] ok, [BAD,C] no, [BAD] no, [C] ok
        Assert.Equal(2, s.Succeeded.Count());
        
        Assert.Equal("refused", s.Failed.Single().Error!.Split(": ").Last());
    }

    [Fact]
    public async Task AlreadyPushedAndUnfirmedOrders_AreNotCombined()
    {
        var (wb, fake) = await Make();
        var done = Po("A", "V1"); done.B1DocEntry = "5";
        var loose = Po("B", "V1"); loose.Firmed = false;
        var s = await wb.PushAsync(new[] { done, loose, Po("C", "V1") }, default, null, DocumentGrouping.Supplier);
        Assert.Single(fake.Posts);
        Assert.Equal(1, Doc(fake.Posts[0].Body).GetProperty("DocumentLines").GetArrayLength());
        Assert.Equal(2, s.Failed.Count());
    }

    [Fact]
    public async Task VeryLargeGroups_AreSplitIntoDocumentsOfAtMost100Lines()
    {
        var (wb, fake) = await Make();
        var orders = Enumerable.Range(0, 230).Select(i => Po($"I{i}", "V1")).ToArray();
        await wb.PushAsync(orders, default, null, DocumentGrouping.Supplier);
        Assert.Equal(new[] { 100, 100, 30 }, fake.Posts.Select(p => Doc(p.Body).GetProperty("DocumentLines").GetArrayLength()));
    }

    [Fact]
    public void Remark_StaysWithinSapsLimit()
    {
        var many = Enumerable.Range(0, 60).Select(i => Po($"I{i}", "V1")).ToList();
        var text = ServiceLayerClient.Remark(many);
        Assert.True(text.Length <= 254, text.Length.ToString());
        Assert.Contains("(60)", text);
        Assert.EndsWith("more", text);
        Assert.StartsWith("Cobalt MRP planned order ", ServiceLayerClient.Remark(new[] { many[0] }));
    }
}
