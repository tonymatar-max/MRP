using System.Net;
using System.Text;
using System.Text.Json;
using CobaltMrp.Api.Services;
using CobaltMrp.Api.Settings;
using CobaltMrp.Data.ServiceLayer;
using CobaltMrp.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CobaltMrp.Api.Tests;

/// <summary>Pushing Cobalt master-data overrides to the SAP B1 item master (against a fake Service Layer).</summary>
public class ItemPushTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cobalt-push-test-" + Guid.NewGuid().ToString("N"));
    public ItemPushTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeSl : HttpMessageHandler
    {
        public Dictionary<string, Dictionary<string, object?>> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, (string Type, string Frozen)> Partners { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Method, string Path, string Body)> Calls { get; } = new();
        public bool IgnorePatch { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath.Replace("/b1s/v1/", ""));
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            Calls.Add((req.Method.Method, path, body));
            if (path.EndsWith("Login")) return Ok("{}");
            if (path.EndsWith("Logout")) return new HttpResponseMessage(HttpStatusCode.NoContent);
            var key = path[(path.IndexOf('\'') + 1)..path.LastIndexOf('\'')].Replace("''", "'");
            if (path.StartsWith("BusinessPartners"))
            {
                if (!Partners.TryGetValue(key, out var bp)) return NotFound();
                return Ok(JsonSerializer.Serialize(new { CardCode = key, CardType = bp.Type, Frozen = bp.Frozen, Valid = "tYES" }));
            }
            if (!Items.TryGetValue(key, out var item)) return NotFound();
            if (req.Method == HttpMethod.Patch)
            {
                if (!IgnorePatch)
                    foreach (var p in JsonDocument.Parse(body).RootElement.EnumerateObject())
                        item[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetDecimal();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return Ok(JsonSerializer.Serialize(item));
        }

        private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound)
        { Content = new StringContent("{\"error\":{\"code\":-2028,\"message\":{\"lang\":\"en-us\",\"value\":\"No matching records found (ODBC -2028)\"}}}", Encoding.UTF8, "application/json") };
    }

    private static Dictionary<string, object?> Sap(string code, string? supplier = null, decimal lead = 0, decimal minOrder = 0, decimal safety = 0) => new()
    {
        ["ItemCode"] = code, ["ItemName"] = code + " name", ["Mainsupplier"] = supplier, ["LeadTime"] = lead, ["MinOrderQuantity"] = minOrder,
        ["OrderMultiple"] = 0m, ["MinInventory"] = safety, ["MaxInventory"] = 0m, ["DesiredInventory"] = 0m,
    };

    private (ItemPushService Svc, FakeSl Fake, MemoryMasterStore Store, MemorySapPushLog Log) Make(bool writeback = true)
    {
        var settings = new SettingsService(_dir, new SecretProtector(new EphemeralDataProtectionProvider()), NullLogger<SettingsService>.Instance);
        var a = settings.Get();
        a.ServiceLayer.Enabled = writeback; a.ServiceLayer.BaseUrl = "http://sl/b1s/v1"; a.ServiceLayer.CompanyDb = "DB"; a.ServiceLayer.UserName = "u"; a.ServiceLayer.Password = "p";
        settings.Save(a);
        var fake = new FakeSl();
        var svc = new ItemPushService(settings, NullLogger<ItemPushService>.Instance, _ =>
            new ServiceLayerClient(new ServiceLayerOptions { BaseUrl = "http://sl/b1s/v1", CompanyDb = "DB", UserName = "u", Password = "p" },
                new HttpClient(fake) { BaseAddress = new Uri("http://sl/b1s/v1/") }));
        return (svc, fake, new MemoryMasterStore(), new MemorySapPushLog());
    }

    private static async Task Save(MemoryMasterStore store, params ItemOverride[] o) => await store.SaveItemsAsync(o, CancellationToken.None);

    [Fact]
    public async Task Preview_ShowsChangeSameBlockedAndCobaltOnly()
    {
        var (svc, fake, store, _) = Make();
        fake.Items["A"] = Sap("A", supplier: "V1", lead: 5, minOrder: 10);
        fake.Partners["V2"] = ("cSupplier", "tNO"); fake.Partners["C1"] = ("cCustomer", "tNO");
        await Save(store,
            new ItemOverride { ItemCode = "A", Supplier = "V2", LeadTimeDays = 5, MinOrderQty = 25, ReorderPoint = 40, PlanningMethod = PlanningMethod.MinMax },
            new ItemOverride { ItemCode = "B", Supplier = "C1", SafetyStock = -3 },
            new ItemOverride { ItemCode = "NOPE", LeadTimeDays = 4 });                  // an override for an item that is not in SAP B1
        fake.Items["B"] = Sap("B");

        var p = await svc.PreviewAsync(await store.GetItemOverridesAsync(default), new[] { "A", "B", "NOPE" }, default);

        Assert.True(p.Ready);
        var a = p.Items.Single(i => i.ItemCode == "A");
        Assert.Equal("A name", a.ItemName);
        Assert.Equal("change", a.Fields.Single(f => f.Field == "supplier").Status);
        Assert.Equal("V1", a.Fields.Single(f => f.Field == "supplier").Sap);
        Assert.Equal("same", a.Fields.Single(f => f.Field == "leadTimeDays").Status);
        Assert.Equal("change", a.Fields.Single(f => f.Field == "minOrderQty").Status);
        Assert.Equal(new[] { "reorderPoint", "planningMethod" }.OrderBy(x => x), a.CobaltOnly.OrderBy(x => x));       // no SAP field for these
        var b = p.Items.Single(i => i.ItemCode == "B");
        Assert.Contains("not a supplier", b.Fields.Single(f => f.Field == "supplier").Message);
        Assert.Equal("blocked", b.Fields.Single(f => f.Field == "safetyStock").Status);
        Assert.False(p.Items.Single(i => i.ItemCode == "NOPE").Found);
        Assert.DoesNotContain(fake.Calls, c => c.Method == "PATCH");                // a preview never writes
    }

    [Fact]
    public async Task Preview_WithWritebackOff_SaysSo_AndTouchesNothing()
    {
        var (svc, fake, store, _) = Make(writeback: false);
        await Save(store, new ItemOverride { ItemCode = "A", LeadTimeDays = 7 });
        var p = await svc.PreviewAsync(await store.GetItemOverridesAsync(default), new[] { "A" }, default);
        Assert.False(p.Ready);
        Assert.Contains("write-back is off", p.NotReadyReason);
        Assert.Empty(fake.Calls);
        await Assert.ThrowsAsync<PushException>(() => svc.ExecuteAsync(new PushExecuteRequest(new() { new("A", new() { new("leadTimeDays", null, "7") }) }, true, true), store, new PushContext(new MemorySapPushLog(), "tony"), default));
    }

    [Fact]
    public async Task Push_WritesOnlyTheChangedFields_ReadsBack_ClearsOnlyThoseOverrides_AndLogs()
    {
        var (svc, fake, store, log) = Make();
        fake.Items["A"] = Sap("A", supplier: "V1", lead: 5, minOrder: 10);
        fake.Partners["V2"] = ("cSupplier", "tNO");
        await Save(store, new ItemOverride { ItemCode = "A", Supplier = "V2", LeadTimeDays = 9, MinOrderQty = 10, ReorderPoint = 40, Notes = "keep me" });

        var req = new PushExecuteRequest(new() { new("A", new()
        {
            new("supplier", "V1", "V2"), new("leadTimeDays", "5", "9"), new("minOrderQty", "10", "10"),
        }) }, Confirm: true, ClearOverrides: true);
        var res = await svc.ExecuteAsync(req, store, new PushContext(log, "tony"), default);

        Assert.Equal(1, res.Pushed);
        var patch = Assert.Single(fake.Calls, c => c.Method == "PATCH");
        Assert.Equal("Items('A')", patch.Path);
        var body = JsonDocument.Parse(patch.Body).RootElement;
        Assert.Equal("V2", body.GetProperty("Mainsupplier").GetString());
        Assert.Equal(9, body.GetProperty("LeadTime").GetInt32());                                   // an integer, not 9.0
        Assert.False(body.TryGetProperty("MinOrderQuantity", out _));                               // SAP B1 already had 10
        Assert.Equal("V2", fake.Items["A"]["Mainsupplier"]);

        var ov = (await store.GetItemOverridesAsync(default))["A"];
        Assert.Null(ov.Supplier); Assert.Null(ov.LeadTimeDays); Assert.Null(ov.MinOrderQty);         // pushed: SAP B1 holds them now
        Assert.Equal(40, ov.ReorderPoint); Assert.Equal("keep me", ov.Notes);                        // Cobalt-only data stays

        var entry = Assert.Single(await log.QueryAsync(new SapPushLogQuery(), default));
        Assert.Equal(SapPushActions.UpdateItem, entry.Action);
        Assert.True(entry.Success); Assert.Equal("OITM:A", entry.Target); Assert.Equal("tony", entry.UserName);
        Assert.Contains("default supplier V1 -> V2", entry.Summary);
    }

    [Fact]
    public async Task Push_WithoutClearing_KeepsTheOverrides()
    {
        var (svc, fake, store, log) = Make();
        fake.Items["A"] = Sap("A", lead: 5);
        await Save(store, new ItemOverride { ItemCode = "A", LeadTimeDays = 9 });
        await svc.ExecuteAsync(new PushExecuteRequest(new() { new("A", new() { new("leadTimeDays", "5", "9") }) }, true, ClearOverrides: false), store, new PushContext(log, "t"), default);
        Assert.Equal(9, (await store.GetItemOverridesAsync(default))["A"].LeadTimeDays);
    }

    [Fact]
    public async Task Push_RefusesWhenSapChangedSincePreview_AndWritesNothing()
    {
        var (svc, fake, store, log) = Make();
        fake.Items["A"] = Sap("A", lead: 12);                                    // someone changed it in SAP B1 after the preview showed 5
        await Save(store, new ItemOverride { ItemCode = "A", LeadTimeDays = 9 });
        var res = await svc.ExecuteAsync(new PushExecuteRequest(new() { new("A", new() { new("leadTimeDays", "5", "9") }) }, true, true), store, new PushContext(log, "t"), default);

        Assert.Equal(1, res.Failed);
        Assert.Contains("changed in SAP B1 since the preview", res.Items[0].Message);
        Assert.DoesNotContain(fake.Calls, c => c.Method == "PATCH");
        Assert.Equal(9, (await store.GetItemOverridesAsync(default))["A"].LeadTimeDays);          // the override is kept
        Assert.False((await log.QueryAsync(new SapPushLogQuery(), default)).Single().Success);
    }

    [Fact]
    public async Task Push_ReportsAFailure_WhenSapAcceptsButDoesNotKeepTheValue()
    {
        var (svc, fake, store, log) = Make();
        fake.Items["A"] = Sap("A", lead: 5); fake.IgnorePatch = true;
        await Save(store, new ItemOverride { ItemCode = "A", LeadTimeDays = 9 });
        var res = await svc.ExecuteAsync(new PushExecuteRequest(new() { new("A", new() { new("leadTimeDays", "5", "9") }) }, true, true), store, new PushContext(log, "t"), default);
        Assert.Equal(1, res.Failed);
        Assert.Contains("accepted the change but", res.Items[0].Message);
        Assert.Equal(9, (await store.GetItemOverridesAsync(default))["A"].LeadTimeDays);          // never cleared on a failure
    }

    [Fact]
    public async Task Push_NeedsConfirmation_AndOneBadItemDoesNotBlockTheOthers()
    {
        var (svc, fake, store, log) = Make();
        fake.Items["A"] = Sap("A", lead: 5);
        await Save(store, new ItemOverride { ItemCode = "A", LeadTimeDays = 9 }, new ItemOverride { ItemCode = "B", LeadTimeDays = 3 });
        await Assert.ThrowsAsync<PushException>(() => svc.ExecuteAsync(new PushExecuteRequest(new() { new("A", new() { new("leadTimeDays", "5", "9") }) }, false, true), store, new PushContext(log, "t"), default));

        var res = await svc.ExecuteAsync(new PushExecuteRequest(new()
        {
            new("B", new() { new("leadTimeDays", "0", "3") }),                    // not in SAP B1
            new("A", new() { new("leadTimeDays", "5", "9") }),
        }, true, true), store, new PushContext(log, "t"), default);
        Assert.Equal((1, 1), (res.Pushed, res.Failed));
        Assert.Contains("not found in SAP B1", res.Items.Single(i => i.ItemCode == "B").Message);
        Assert.Equal(9m, Convert.ToDecimal(fake.Items["A"]["LeadTime"]));
    }

    [Fact]
    public async Task Push_RejectsFieldsWithNoSapEquivalent_AndBadValues()
    {
        var (svc, fake, store, log) = Make();
        fake.Items["A"] = Sap("A");
        await Save(store, new ItemOverride { ItemCode = "A", ReorderPoint = 5 });
        var r1 = await svc.ExecuteAsync(new PushExecuteRequest(new() { new("A", new() { new("reorderPoint", null, "5") }) }, true, true), store, new PushContext(log, "t"), default);
        Assert.Contains("cannot be pushed", r1.Items[0].Message);
        var r2 = await svc.ExecuteAsync(new PushExecuteRequest(new() { new("A", new() { new("leadTimeDays", null, "5000") }) }, true, true), store, new PushContext(log, "t"), default);
        Assert.Contains("0 to 3650", r2.Items[0].Message);
        Assert.DoesNotContain(fake.Calls, c => c.Method == "PATCH");
    }

    [Fact]
    public async Task ItemCodesWithQuotesAndSpaces_AreEscaped()
    {
        var (svc, fake, store, log) = Make();
        fake.Items["O'Neil & Co"] = Sap("O'Neil & Co", lead: 1);
        await Save(store, new ItemOverride { ItemCode = "O'Neil & Co", LeadTimeDays = 4 });
        var res = await svc.ExecuteAsync(new PushExecuteRequest(new() { new("O'Neil & Co", new() { new("leadTimeDays", "1", "4") }) }, true, true), store, new PushContext(log, "t"), default);
        Assert.Equal(1, res.Pushed);
        Assert.Contains(fake.Calls, c => c.Method == "PATCH" && c.Path == "Items('O''Neil & Co')");
    }
}
