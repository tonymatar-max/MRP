using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using CobaltMrp.Api.Services;
using CobaltMrp.Api.Settings;
using CobaltMrp.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CobaltMrp.Api.Tests;

/// <summary>
/// ReleaseService and ExceptionExecutor against a small in-process Service Layer stand-in (real HTTP on localhost):
/// QA round 1 defects D4 (never a 500), D7 (close one line, header date), D8 (messages, password) and D9 (re-release).
/// </summary>
public sealed class ServiceLayerWriteTests : IDisposable
{
    private sealed class FakeSl : IDisposable
    {
        private readonly HttpListener _l = new();
        public int Port { get; }
        public List<string> Requests { get; } = new();
        public JsonNode Po = JsonNode.Parse("""
            {"DocEntry":900,"DocumentStatus":"bost_Open","DocDueDate":"2026-11-20T00:00:00Z","DocumentLines":[
              {"LineNum":0,"ItemCode":"A","Quantity":1,"RemainingOpenQuantity":1,"ShipDate":"2026-11-20T00:00:00Z","LineStatus":"bost_Open"},
              {"LineNum":1,"ItemCode":"B","Quantity":1,"RemainingOpenQuantity":1,"ShipDate":"2026-11-25T00:00:00Z","LineStatus":"bost_Open"}]}
            """)!;
        public int NextDoc = 1000;
        public Func<string, string, (int Status, string Body)?>? Override;

        public FakeSl()
        {
            var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); Port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
            _l.Prefixes.Add($"http://localhost:{Port}/"); _l.Start();
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_l.IsListening)
            {
                HttpListenerContext c;
                try { c = await _l.GetContextAsync(); } catch { return; }
                try { Handle(c); } catch (Exception ex) { Reply(c, 500, ex.Message); }
            }
        }

        private void Handle(HttpListenerContext c)
        {
            var path = c.Request.Url!.AbsolutePath.Replace("/b1s/v1/", "");
            var body = new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd();
            var key = $"{c.Request.HttpMethod} {path}";
            lock (Requests) Requests.Add(body.Length > 0 ? key + " " + body : key);
            if (Override?.Invoke(key, body) is { } o) { Reply(c, o.Status, o.Body); return; }
            if (path == "Login" || path == "Logout") { Reply(c, 200, "{}"); return; }
            if (key == "GET PurchaseOrders(900)") { Reply(c, 200, Po.ToJsonString()); return; }
            if (key == "PATCH PurchaseOrders(900)")
            {
                var patch = JsonNode.Parse(body)!;
                foreach (var kv in patch.AsObject())
                {
                    if (kv.Key == "DocumentLines")
                        foreach (var l in kv.Value!.AsArray())
                        {
                            var target = Po["DocumentLines"]!.AsArray().First(x => (int)x!["LineNum"]! == (int)l!["LineNum"]!)!;
                            foreach (var f in l.AsObject()) target[f.Key] = f.Key == "ShipDate" ? f.Value!.ToString() + "T00:00:00Z" : f.Value!.DeepClone();
                        }
                    else Po[kv.Key] = kv.Key == "DocDueDate" ? kv.Value!.ToString() + "T00:00:00Z" : kv.Value!.DeepClone();
                }
                Reply(c, 204, ""); return;
            }
            if (key == "POST PurchaseOrders(900)/Close") { Po["DocumentStatus"] = "bost_Close"; Reply(c, 204, ""); return; }
            if (key.StartsWith("GET Items(")) { Reply(c, 200, """{"UoMGroupEntry":-1,"InventoryUoMEntry":1}"""); return; }
            if (key.StartsWith("GET ")) { Reply(c, 200, """{"value":[]}"""); return; }
            if (key.StartsWith("POST PurchaseOrders")) { var n = NextDoc++; Reply(c, 201, $"{{\"DocEntry\":{n}}}"); return; }
            Reply(c, 404, "{\"error\":{\"code\":-1,\"message\":{\"value\":\"not found\"}}}");
        }

        private static void Reply(HttpListenerContext c, int status, string body)
        {
            c.Response.StatusCode = status; c.Response.ContentType = "application/json";
            var b = Encoding.UTF8.GetBytes(body); c.Response.ContentLength64 = b.Length;
            c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
        }

        public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cobalt-slwrite-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<FakeSl> _fakes = new();
    public ServiceLayerWriteTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { foreach (var f in _fakes) f.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    private FakeSl StartFake() { var f = new FakeSl(); _fakes.Add(f); return f; }

    private SettingsService Settings(int port, bool enabled = true)
    {
        var s = new SettingsService(_dir, new SecretProtector(new EphemeralDataProtectionProvider()), NullLogger<SettingsService>.Instance);
        var a = new AppSettings();
        a.ServiceLayer.Enabled = enabled; a.ServiceLayer.BaseUrl = $"http://localhost:{port}/b1s/v1"; a.ServiceLayer.CompanyDb = "DB";
        a.ServiceLayer.UserName = "u"; a.ServiceLayer.Password = "p"; a.ServiceLayer.DefaultVendor = "V1";
        s.Save(a);
        return s;
    }

    private static PlannedOrder Order(string item, string? docEntry = null) => new()
    {
        ItemCode = item, WarehouseCode = "01", Type = PlannedOrderType.PurchaseOrder, Quantity = 5, Firmed = true, B1DocEntry = docEntry,
        DueDate = new DateOnly(2026, 12, 1), ReleaseDate = new DateOnly(2026, 11, 20),
    };

    // ---- D9 --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReReleasingPushedOrders_IsNotAFailure_AndIsNotLogged()
    {
        var sl = StartFake();
        var log = new MemorySapPushLog();
        var svc = new ReleaseService(Settings(sl.Port), NullLogger<ReleaseService>.Instance);
        var done = Order("A", "77"); var fresh = Order("B");
        var run = new MemoryRunStore();

        var outcome = await svc.ReleaseAsync(run, new[] { done, fresh }, default, new PushContext(log, "tony"), DocumentGrouping.Supplier);

        Assert.Empty(outcome.Failures);
        Assert.Equal(1, outcome.AlreadyInSap);
        Assert.Equal(1, outcome.Documents);
        var entries = await log.QueryAsync(new SapPushLogQuery(), default);
        Assert.Single(entries);                                          // only the order really sent
        Assert.Equal("B", entries[0].ItemCode);
    }

    [Fact]
    public async Task ReReleasingOnlyPushedOrders_PostsNothing_NoFailures()
    {
        var sl = StartFake();
        var svc = new ReleaseService(Settings(sl.Port), NullLogger<ReleaseService>.Instance);
        var outcome = await svc.ReleaseAsync(new MemoryRunStore(), new[] { Order("A", "77"), Order("B", "78") }, default, new PushContext(new MemorySapPushLog(), "t"));
        Assert.Empty(outcome.Failures);
        Assert.Equal((0, 2), (outcome.Pushed, outcome.AlreadyInSap));
        Assert.DoesNotContain(sl.Requests, r => r.StartsWith("POST PurchaseOrders"));
    }

    // ---- D4: nothing escapes as an HTTP 500 -------------------------------------------------------------------------

    [Fact]
    public async Task ServiceLayerDown_ReleaseReturnsAFailure_InsteadOfThrowing()
    {
        var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); var closedPort = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
        var log = new MemorySapPushLog();
        var svc = new ReleaseService(Settings(closedPort), NullLogger<ReleaseService>.Instance);
        var outcome = await svc.ReleaseAsync(new MemoryRunStore(), new[] { Order("A") }, default, new PushContext(log, "t"));
        var f = Assert.Single(outcome.Failures);
        Assert.StartsWith("Service Layer:", f.Error);
        Assert.Single(await log.QueryAsync(new SapPushLogQuery(Action: SapPushActions.Session), default));
    }

    [Fact]
    public async Task ConnectionDropsOnOneDocument_OtherDocumentsAreStillCreated_AndEveryOrderIsReported()
    {
        var sl = StartFake();
        var posts = 0;
        sl.Override = (key, _) =>
        {
            if (!key.StartsWith("POST PurchaseOrders")) return null;
            return ++posts == 2 ? (500, "<html>boom</html>") : null;
        };
        var s = Settings(sl.Port);
        var cfg = s.Get(); cfg.ServiceLayer.ItemVendors = new() { new ItemVendor { ItemCode = "A", CardCode = "V1" }, new ItemVendor { ItemCode = "B", CardCode = "V2" }, new ItemVendor { ItemCode = "C", CardCode = "V3" } }; s.Save(cfg);
        var svc = new ReleaseService(s, NullLogger<ReleaseService>.Instance);
        var orders = new[] { Order("A"), Order("B"), Order("C") };
        var outcome = await svc.ReleaseAsync(new MemoryRunStore(), orders, default, new PushContext(new MemorySapPushLog(), "t"), DocumentGrouping.Supplier);
        Assert.Equal(2, outcome.Pushed);
        var f = Assert.Single(outcome.Failures);
        Assert.Equal(orders[1].Id, f.PlannedOrderId);
        Assert.DoesNotContain("<", f.Error);                              // an HTML body never reaches the planner
    }

    // ---- D7: close one line of a grouped purchase order -------------------------------------------------------------

    private static (ExceptionExecutor, MrpRun, ExceptionMessage) Executor(FakeSl sl, SettingsService settings, ExceptionType type, DateOnly? current, DateOnly? proposed, int line)
    {
        var log = NullLogger<ExceptionExecutor>.Instance;
        var exec = new ExceptionExecutor(settings, new ReleaseService(settings, NullLogger<ReleaseService>.Instance), log);
        var run = new MrpRun();
        var ex = new ExceptionMessage
        {
            Type = type, ItemCode = line == 0 ? "A" : "B", SourceDocKey = "OPOR:900", SourceLine = line, CurrentDate = current, ProposedDate = proposed, Quantity = 1,
        };
        run.Exceptions.Add(ex);
        return (exec, run, ex);
    }

    [Fact]
    public async Task Close_OnAMultiLinePurchaseOrder_ClosesOnlyThatLine()
    {
        var sl = StartFake();
        var settings = Settings(sl.Port);
        var (exec, run, ex) = Executor(sl, settings, ExceptionType.Cancel, new DateOnly(2026, 11, 25), null, 1);

        var preview = await exec.PreviewAsync(run, ex, new ExecuteRequest("Close", null, null, false), default);
        Assert.True(preview.CanExecute, string.Join("|", preview.Blockers));
        Assert.Contains("line 1", preview.Summary);
        Assert.Contains("stay open", preview.Summary);

        var result = await exec.ExecuteAsync(new MemoryRunStore(), run, ex, new ExecuteRequest("Close", null, null, true), "tony", new MemorySapPushLog(), default);
        Assert.True(result.Ok);
        Assert.Contains("Line 1", result.Message);
        Assert.DoesNotContain(sl.Requests, r => r.Contains("/Close"));                        // not the whole document
        Assert.Contains(sl.Requests, r => r.StartsWith("PATCH PurchaseOrders(900)") && r.Contains("bost_Close") && r.Contains("\"LineNum\":1"));
        Assert.Equal("bost_Open", (string)sl.Po["DocumentLines"]![0]!["LineStatus"]!);        // line 0 untouched
        Assert.Equal("bost_Close", (string)sl.Po["DocumentLines"]![1]!["LineStatus"]!);
    }

    [Fact]
    public async Task Close_OnTheOnlyOpenLine_ClosesTheWholeDocument()
    {
        var sl = StartFake();
        sl.Po["DocumentLines"]![0]!["LineStatus"] = "bost_Close";
        var settings = Settings(sl.Port);
        var (exec, run, ex) = Executor(sl, settings, ExceptionType.Cancel, new DateOnly(2026, 11, 25), null, 1);
        var result = await exec.ExecuteAsync(new MemoryRunStore(), run, ex, new ExecuteRequest("Close", null, null, true), "tony", new MemorySapPushLog(), default);
        Assert.Contains(sl.Requests, r => r == "POST PurchaseOrders(900)/Close");
        Assert.Contains("closed", result.Message);
    }

    [Fact]
    public async Task Move_TheEarliestLine_AlsoMovesTheHeaderDueDate()
    {
        var sl = StartFake();
        var settings = Settings(sl.Port);
        var newDate = DateOnly.FromDateTime(DateTime.Today).AddDays(400);
        var (exec, run, ex) = Executor(sl, settings, ExceptionType.Defer, new DateOnly(2026, 11, 20), newDate, 0);
        var result = await exec.ExecuteAsync(new MemoryRunStore(), run, ex, new ExecuteRequest("MoveDueDate", newDate, null, true), "tony", new MemorySapPushLog(), default);
        Assert.Contains("Header due date", result.Message);
        // Line 1 (2026-11-25) is now the earliest open line, so the header follows it.
        Assert.StartsWith("2026-11-25", (string)sl.Po["DocDueDate"]!);
    }

    [Fact]
    public async Task Move_ALaterLine_LeavesTheHeaderAlone()
    {
        var sl = StartFake();
        var settings = Settings(sl.Port);
        var newDate = DateOnly.FromDateTime(DateTime.Today).AddDays(400);
        var (exec, run, ex) = Executor(sl, settings, ExceptionType.Defer, new DateOnly(2026, 11, 25), newDate, 1);
        await exec.ExecuteAsync(new MemoryRunStore(), run, ex, new ExecuteRequest("MoveDueDate", newDate, null, true), "tony", new MemorySapPushLog(), default);
        Assert.StartsWith("2026-11-20", (string)sl.Po["DocDueDate"]!);
    }

    // ---- D8 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ChangingTheBaseUrl_WithAnEmptyPassword_KeepsTheStoredPassword()
    {
        var s = new SettingsService(_dir, new SecretProtector(new EphemeralDataProtectionProvider()), NullLogger<SettingsService>.Instance);
        var a = new AppSettings(); a.ServiceLayer.BaseUrl = "https://a:50000/b1s/v1"; a.ServiceLayer.CompanyDb = "D"; a.ServiceLayer.UserName = "u"; a.ServiceLayer.Password = "secret";
        s.Save(a);
        var edit = SettingsService.Masked(s.Get());                        // what the UI GETs: password blank
        edit.ServiceLayer.BaseUrl = "  https://b:50000/b1s/v1  ";
        var saved = s.Save(edit);
        Assert.Equal("secret", saved.ServiceLayer.Password);               // not dropped when the URL changes
        Assert.Equal("https://b:50000/b1s/v1", saved.ServiceLayer.BaseUrl); // stray spaces trimmed
    }

    [Theory]
    [InlineData("", "Enter the base URL")]
    [InlineData("https://a b:50000/b1s/v1", "contains spaces")]
    [InlineData("ftp://a/b1s", "not a valid web address")]
    [InlineData("tonypc:50000", "not a valid web address")]
    public void BaseUrlValidation_SaysWhatIsWrong(string url, string expected) =>
        Assert.Contains(expected, ServiceLayerDiagnostics.ValidateBaseUrl(url));

    [Fact]
    public void BaseUrlValidation_AcceptsNormalUrls() => Assert.Null(ServiceLayerDiagnostics.ValidateBaseUrl("https://tonypc:50000/b1s/v1"));

    [Fact]
    public void CertificateFailure_NamesTheCertificate_AndTheSetting()
    {
        var inner = new System.Security.Authentication.AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot");
        var ex = new HttpRequestException("The SSL connection could not be established, see inner exception.", inner);
        var msg = ServiceLayerDiagnostics.Explain(ex, "https://127.0.0.1:50000/b1s/v1", false);
        Assert.Contains("certificate", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Allow invalid certificates", msg);
    }

    [Fact]
    public void HttpAgainstTheHttpsPort_SuggestsHttps()
    {
        var ex = new HttpRequestException("An error occurred while sending the request.", new IOException("The response ended prematurely."));
        var msg = ServiceLayerDiagnostics.Explain(ex, "http://tonypc:50000/b1s/v1", false);
        Assert.Contains("https://tonypc:50000/b1s/v1", msg);
    }

    [Fact]
    public void ConnectionRefused_AndUnknownHost_AreReadable()
    {
        var refused = new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused));
        Assert.Contains("connection refused", ServiceLayerDiagnostics.Explain(refused, "https://h:1/b1s/v1", false));
        var nohost = new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound));
        Assert.Contains("could not be found", ServiceLayerDiagnostics.Explain(nohost, "https://nohost/b1s/v1", false));
    }
}
