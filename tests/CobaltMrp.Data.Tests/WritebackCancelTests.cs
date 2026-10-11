using System.Net;
using System.Text;
using CobaltMrp.Data.ServiceLayer;
using CobaltMrp.Domain;
using Xunit;

namespace CobaltMrp.Data.Tests;

public class WritebackCancelTests
{
    private sealed class CancelOnSecondPost(CancellationTokenSource cts) : HttpMessageHandler
    {
        private int _posts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var json = req.RequestUri!.AbsolutePath.EndsWith("Login") ? "{}" : "{\"DocEntry\":777}";
            if (req.Method == HttpMethod.Post && !req.RequestUri.AbsolutePath.EndsWith("Login") && ++_posts == 2) cts.Cancel();   // the user cancels while the second order is being sent
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private static PlannedOrder Po(string item) => new()
    {
        ItemCode = item, WarehouseCode = "WH01", Type = PlannedOrderType.PurchaseOrder, Quantity = 5, Firmed = true,
        DueDate = new DateOnly(2026, 11, 1), ReleaseDate = new DateOnly(2026, 10, 20),
    };

    [Fact]
    public async Task Cancelled_MidBatch_KeepsTheResultsOfOrdersAlreadyCreated()
    {
        using var cts = new CancellationTokenSource();
        var http = new HttpClient(new CancelOnSecondPost(cts)) { BaseAddress = new Uri("http://sl/b1s/v1/") };
        using var client = new ServiceLayerClient(new ServiceLayerOptions { BaseUrl = "http://sl/b1s/v1", CompanyDb = "DB", UserName = "u", Password = "p" }, http);
        await client.LoginAsync();
        var a = Po("A"); var b = Po("B");
        var into = new WritebackSummary();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PlannedOrderWriteback(client, _ => "V001").PushAsync(new[] { a, b }, cts.Token, into));

        var done = Assert.Single(into.Results);                 // order A finished; B was interrupted, so nothing is claimed about it
        Assert.True(done.Success);
        Assert.Equal(777, done.DocEntry);
        Assert.Equal("777", a.B1DocEntry);                      // remembered, so a retry will not create it again
        Assert.Null(b.B1DocEntry);
    }
}
