using System.Net;
using System.Text;
using CobaltMrp.Data.ServiceLayer;
using CobaltMrp.Domain;
using Xunit;

namespace CobaltMrp.Data.Tests;

public class ServiceLayerClientTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public List<(string Path, string Body)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls.Add((req.RequestUri!.AbsolutePath, req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct)));
            var json = req.RequestUri!.AbsolutePath.EndsWith("Login") ? "{}" : "{\"DocEntry\":42,\"AbsoluteEntry\":43}";
            return new HttpResponseMessage(HttpStatusCode.Created)
                { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static (ServiceLayerClient, Capture) Make()
    {
        var cap = new Capture();
        var http = new HttpClient(cap) { BaseAddress = new Uri("http://sl/b1s/v1/") };
        var c = new ServiceLayerClient(new ServiceLayerOptions
            { BaseUrl = "http://sl/b1s/v1", CompanyDb = "DB", UserName = "u", Password = "p" }, http);
        return (c, cap);
    }

    private static PlannedOrder Order(PlannedOrderType t) => new()
    {
        ItemCode = "X", WarehouseCode = "01", Type = t, Quantity = 5, Firmed = true,
        DueDate = new DateOnly(2026, 12, 1), ReleaseDate = new DateOnly(2026, 11, 25),
    };

    [Fact]
    public async Task PurchaseOrder_PayloadUsesPascalCaseNames()
    {
        var (c, cap) = Make();
        await c.LoginAsync();
        var doc = await c.CreatePurchaseOrderAsync(Order(PlannedOrderType.PurchaseOrder), "V001");
        Assert.Equal(42, doc);
        var body = cap.Calls.Last().Body;
        Assert.Contains("\"CardCode\":\"V001\"", body);
        Assert.Contains("\"DocumentLines\":[", body);
        Assert.Contains("\"ItemCode\":\"X\"", body);
        Assert.DoesNotContain("\"cardCode\"", body);
    }

    [Fact]
    public async Task ProductionOrder_PayloadUsesPascalCaseNames()
    {
        var (c, cap) = Make();
        await c.LoginAsync();
        await c.CreateProductionOrderAsync(Order(PlannedOrderType.ProductionOrder));
        var body = cap.Calls.Last().Body;
        Assert.Contains("\"ItemNo\":\"X\"", body);
        Assert.Contains("\"PlannedQuantity\":5", body);
        Assert.Contains("\"ProductionOrderStatus\":\"boposPlanned\"", body);
    }

    [Fact]
    public async Task Login_SendsCompanyDbUserAndPassword()
    {
        var (c, cap) = Make();
        await c.LoginAsync();
        Assert.Contains("\"CompanyDB\":\"DB\"", cap.Calls[0].Body);
    }

    [Fact]
    public async Task ErrorResponse_WithNumericCode_SurfacesB1Message()
    {
        var http = new HttpClient(new Fail()) { BaseAddress = new Uri("http://sl/b1s/v1/") };
        var c = new ServiceLayerClient(new ServiceLayerOptions
            { BaseUrl = "http://sl/b1s/v1", CompanyDb = "DB", UserName = "u", Password = "p" }, http);
        var ex = await Assert.ThrowsAsync<ServiceLayerException>(() => c.LoginAsync());
        Assert.Contains("Invalid password", ex.Message);
        Assert.DoesNotContain("\"code\"", ex.Message);   // friendly text, not the raw body
    }

    private sealed class Fail : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    "{\"error\":{\"code\":-5002,\"message\":{\"lang\":\"en-us\",\"value\":\"Invalid password\"}}}",
                    Encoding.UTF8, "application/json"),
            });
    }
}
