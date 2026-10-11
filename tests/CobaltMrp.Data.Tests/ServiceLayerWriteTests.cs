using System.Net;
using System.Text;
using CobaltMrp.Data.ServiceLayer;
using Xunit;

namespace CobaltMrp.Data.Tests;

/// <summary>Reading, patching and closing existing documents: exact URLs, headers and PascalCase bodies.</summary>
public class ServiceLayerWriteTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body, string? ReplaceHeader)> Calls { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.NoContent;
        public string ResponseJson { get; set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls.Add((req.Method, req.RequestUri!.PathAndQuery, req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct),
                req.Headers.TryGetValues("B1S-ReplaceCollectionsOnPatch", out var v) ? v.First() : null));
            if (req.RequestUri.AbsolutePath.EndsWith("Login"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            var r = new HttpResponseMessage(Status);
            if (ResponseJson.Length > 0) r.Content = new StringContent(ResponseJson, Encoding.UTF8, "application/json");
            return r;
        }
    }

    private static async Task<(ServiceLayerClient Client, Capture Cap)> Make()
    {
        var cap = new Capture();
        var http = new HttpClient(cap) { BaseAddress = new Uri("http://sl/b1s/v1/") };
        var c = new ServiceLayerClient(new ServiceLayerOptions { BaseUrl = "http://sl/b1s/v1", CompanyDb = "DB", UserName = "u", Password = "p" }, http);
        await c.LoginAsync();
        return (c, cap);
    }

    [Fact]
    public async Task Patch_SendsPascalCaseLineUpdate_AndNeverReplacesTheCollection()
    {
        var (c, cap) = await Make();
        await c.PatchEntityAsync("PurchaseOrders(4501)", new { DocumentLines = new[] { new { LineNum = 2, ShipDate = "2026-10-14" } } });
        var call = cap.Calls.Last();
        Assert.Equal(HttpMethod.Patch, call.Method);
        Assert.EndsWith("/PurchaseOrders(4501)", call.Path);
        Assert.Contains("\"DocumentLines\":[{\"LineNum\":2,\"ShipDate\":\"2026-10-14\"}]", call.Body);
        Assert.Equal("false", call.ReplaceHeader);      // other lines of the order must be left alone
    }

    [Fact]
    public async Task Close_PostsTheBoundAction()
    {
        var (c, cap) = await Make();
        await c.PostActionAsync("PurchaseOrders(4501)/Close");
        Assert.Equal(HttpMethod.Post, cap.Calls.Last().Method);
        Assert.EndsWith("/PurchaseOrders(4501)/Close", cap.Calls.Last().Path);
    }

    [Fact]
    public async Task Get_ReturnsTheDocumentAsJson()
    {
        var (c, cap) = await Make();
        cap.Status = HttpStatusCode.OK;
        cap.ResponseJson = "{\"DocEntry\":4501,\"DocumentStatus\":\"bost_Open\",\"DocumentLines\":[{\"LineNum\":0,\"ShipDate\":\"2026-10-18\"}]}";
        var j = await c.GetEntityAsync("PurchaseOrders(4501)");
        Assert.Equal("bost_Open", j.GetProperty("DocumentStatus").GetString());
        Assert.Equal("2026-10-18", j.GetProperty("DocumentLines")[0].GetProperty("ShipDate").GetString());
    }

    [Fact]
    public async Task Errors_SurfaceTheServiceLayerMessage()
    {
        var (c, cap) = await Make();
        cap.Status = HttpStatusCode.BadRequest;
        cap.ResponseJson = "{\"error\":{\"code\":-5002,\"message\":{\"lang\":\"en-us\",\"value\":\"Document is closed\"}}}";
        var ex = await Assert.ThrowsAsync<ServiceLayerException>(() => c.PatchEntityAsync("PurchaseOrders(1)", new { DocDueDate = "2026-01-01" }));
        Assert.Contains("Document is closed", ex.Message);
    }

    [Fact]
    public async Task WritesRequireLogin()
    {
        var http = new HttpClient(new Capture()) { BaseAddress = new Uri("http://sl/b1s/v1/") };
        var c = new ServiceLayerClient(new ServiceLayerOptions { BaseUrl = "http://sl/b1s/v1", CompanyDb = "DB", UserName = "u", Password = "p" }, http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.PatchEntityAsync("PurchaseOrders(1)", new { X = 1 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.PostActionAsync("PurchaseOrders(1)/Close"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.GetEntityAsync("PurchaseOrders(1)"));
    }
}
