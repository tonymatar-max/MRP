using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CobaltMrp.Api.Backup;
using CobaltMrp.Api.Licensing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CobaltMrp.Api.Tests;

/// <summary>Licence verification, the 402 gate and who may save a key. Everything signs with a throwaway RSA key.</summary>
public class LicenseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cobalt-lic-test-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly string _spki;
    private readonly string _hw;

    public LicenseTests()
    {
        Directory.CreateDirectory(Data);
        _spki = Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo());
        _hw = LicenseService.ComputeHardwareKey(Environment.MachineName, _root);
    }
    public void Dispose() { _rsa.Dispose(); try { Directory.Delete(_root, true); } catch { } }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Same construction as the generator's LicenseSigner.CreateLicenseKey.
    private string Token(string product, string hardware, DateTime expires, RSA? signer = null, string? customer = null, int? seats = null)
    {
        var json = JsonSerializer.Serialize(new { SchemaVersion = "1", ProductCode = product, HardwareKey = hardware, ExpiresOn = expires.Date, MaxSeats = seats, Customer = customer });
        var payload = B64Url(Encoding.UTF8.GetBytes(json));
        var sig = B64Url((signer ?? _rsa).SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return $"ICL1.{payload}.{sig}";
    }
    private LicenseService Svc() => new(_root, Data, NullLogger.Instance, _spki);
    private static readonly DateTime Future = DateTime.UtcNow.Date.AddDays(30);
    private string Good => Token(LicenseDefaults.ProductCode, _hw, Future);

    // ---------------------------------------------------------------- verifier

    [Fact] public void NoFile_IsNotLicensed()
    {
        var s = Svc().Load();
        Assert.False(s.IsValid); Assert.Equal(LicenseState.NotLicensed, s.State); Assert.False(s.LicenseKeySet);
        Assert.False(Svc().IsValid());
    }

    [Fact] public void ValidKey_IsAccepted_AndStoredInTheDataFolder()
    {
        var svc = Svc();
        var r = svc.Save(Good);
        Assert.True(r.Saved); Assert.True(r.Status.IsValid);
        Assert.True(File.Exists(Path.Combine(Data, "license.json")));                 // data\ survives a republish
        Assert.False(File.Exists(Path.Combine(_root, "license.json")));
        Assert.True(Svc().Load().IsValid);                                            // a fresh instance (restart) still sees it
        Assert.Equal(Future, Svc().Load().ExpiresOn);
    }

    [Fact] public void ProductCode_IsCaseInsensitive() =>
        Assert.True(Svc().Save(Token(LicenseDefaults.ProductCode.ToUpperInvariant(), _hw, Future)).Saved);

    [Fact] public void ExpiresToday_IsStillValid() =>
        Assert.True(Svc().Save(Token(LicenseDefaults.ProductCode, _hw, DateTime.UtcNow.Date)).Saved);

    [Fact] public void ExpiredYesterday_IsRejected_WithTheExpiryDate()
    {
        var r = Svc().Save(Token(LicenseDefaults.ProductCode, _hw, DateTime.UtcNow.Date.AddDays(-1)));
        Assert.False(r.Saved); Assert.Equal(LicenseState.Expired, r.Status.State);
        Assert.Contains(DateTime.UtcNow.Date.AddDays(-1).ToString("yyyy-MM-dd"), r.Status.Message);
    }

    [Fact] public void WrongHardwareKey_IsRejected()
    {
        var r = Svc().Save(Token(LicenseDefaults.ProductCode, new string('A', 32), Future));
        Assert.False(r.Saved); Assert.Equal(LicenseState.WrongMachine, r.Status.State);
    }

    [Fact] public void WrongProduct_IsRejected()
    {
        var r = Svc().Save(Token("CobaltWMS", _hw, Future));
        Assert.False(r.Saved); Assert.Equal(LicenseState.WrongProduct, r.Status.State);
    }

    [Fact] public void SignedByAnotherKey_IsRejected()
    {
        using var rogue = RSA.Create(2048);
        var r = Svc().Save(Token(LicenseDefaults.ProductCode, _hw, Future, rogue));
        Assert.False(r.Saved); Assert.Equal(LicenseState.BadSignature, r.Status.State);
    }

    [Fact] public void TamperedPayload_UnderAnOldSignature_IsRejected()
    {
        var parts = Good.Split('.');
        var later = Token(LicenseDefaults.ProductCode, _hw, Future.AddYears(10)).Split('.');
        var r = Svc().Save($"ICL1.{later[1]}.{parts[2]}");
        Assert.False(r.Saved); Assert.Equal(LicenseState.BadSignature, r.Status.State);
        Assert.Null(r.Status.ExpiresOn);                                               // nothing in an unverified payload is echoed
    }

    [Theory]
    [InlineData("not-a-licence")] [InlineData("")] [InlineData("   ")] [InlineData("ICL2.a.b")] [InlineData("ICL1.only-two")]
    public void Garbage_IsMalformedOrMissing(string key)
    {
        var r = Svc().Save(key);
        Assert.False(r.Saved); Assert.False(r.Status.IsValid);
    }

    [Fact] public void TruncatedSignature_IsRejected()
    {
        var p = Good.Split('.');
        Assert.False(Svc().Save($"ICL1.{p[1]}.{p[2][..^6]}").Saved);
    }

    [Fact] public void SurroundingWhitespace_IsAccepted() => Assert.True(Svc().Save("  " + Good + "\r\n").Saved);

    [Fact] public void InvalidKey_NeverReplacesAGoodOne()
    {
        var svc = Svc();
        Assert.True(svc.Save(Good).Saved);
        Assert.False(svc.Save("junk").Saved);
        Assert.False(svc.Save(Token(LicenseDefaults.ProductCode, _hw, DateTime.UtcNow.Date.AddDays(-5))).Saved);
        Assert.True(svc.Load().IsValid);
        Assert.True(Svc().IsValid());
    }

    [Fact] public void Save_ClearsTheCache_Immediately()
    {
        var svc = Svc();
        Assert.False(svc.IsValid());
        svc.Save(Good);
        Assert.True(svc.IsValid());
    }

    [Fact] public void CorruptFile_MeansUnlicensed_NotACrash()
    {
        File.WriteAllText(Path.Combine(Data, "license.json"), "{ this is not json");
        var svc = Svc();
        Assert.False(svc.IsValid());
        Assert.Equal(LicenseState.NotLicensed, svc.Load().State);
        Assert.True(svc.Save(Good).Saved);                                             // and a good key repairs it
    }

    [Fact] public void KeyFileEditedToExpired_IsUnlicensedOnReload()
    {
        File.WriteAllText(Path.Combine(Data, "license.json"),
            JsonSerializer.Serialize(new StoredLicense { LicenseKey = Token(LicenseDefaults.ProductCode, _hw, DateTime.UtcNow.Date.AddDays(-1)) }));
        Assert.Equal(LicenseState.Expired, Svc().Load().State);
    }

    [Fact] public void Status_ShowsCustomerAndSeats_WhenPresent_AndNeverTheKey()
    {
        var svc = Svc();
        var key = Token(LicenseDefaults.ProductCode, _hw, Future, customer: "Acme Trading", seats: 7);
        svc.Save(key);
        var s = svc.Load();
        Assert.Equal("Acme Trading", s.Customer); Assert.Equal(7, s.MaxSeats);
        Assert.DoesNotContain(key.Split('.')[2], JsonSerializer.Serialize(s));
    }

    [Fact] public void HardwareKey_IsFormatted_FolderBound_AndStableAcrossTrailingSlash()
    {
        Assert.Equal(32, _hw.Length); Assert.True(_hw.All(Uri.IsHexDigit)); Assert.Equal(_hw.ToUpperInvariant(), _hw);
        Assert.NotEqual(_hw, LicenseService.ComputeHardwareKey(Environment.MachineName, _root + "2"));
        var a = new LicenseService(_root, Data, NullLogger.Instance, _spki).Load().HardwareKey;
        var b = new LicenseService(_root + Path.DirectorySeparatorChar, Data, NullLogger.Instance, _spki).Load().HardwareKey;
        Assert.Equal(a, b); Assert.Equal(_hw, a);
    }

    [Fact] public void PublicKeyOverride_IsHonouredOnlyInDevelopment()
    {
        Assert.Equal(_spki, LicenseDefaults.ResolvePublicKey(true, _spki));
        Assert.Equal(LicenseDefaults.PublicKeySpki, LicenseDefaults.ResolvePublicKey(false, _spki));      // Production ignores it
        Assert.Equal(LicenseDefaults.PublicKeySpki, LicenseDefaults.ResolvePublicKey(true, null));
        Assert.Equal(LicenseDefaults.PublicKeySpki, LicenseDefaults.ResolvePublicKey(true, "  "));
        // and a key signed with the throwaway pair does NOT verify against the embedded production key
        Assert.False(new LicenseService(_root, Data, NullLogger.Instance).Save(Good).Saved);
    }

    // ---------------------------------------------------------------- gate

    private sealed class FakeLicense(bool valid) : ILicenseService
    {
        public LicenseStatus Load() => new(LicenseDefaults.ProductCode, "HW", valid ? LicenseState.Valid : LicenseState.NotLicensed, valid, valid, null, null, null, valid ? "Licensed." : "No licence key has been entered for this server yet.");
        public LicenseSaveResult Save(string k) => new(false, Load());
        public bool IsValid() => valid;
    }

    private static async Task<(int Status, string Body, bool Reached)> Call(bool licensed, string path, string method = "GET")
    {
        var services = new ServiceCollection().AddSingleton<ILicenseService>(new FakeLicense(licensed)).BuildServiceProvider();
        var reached = false;
        var app = new ApplicationBuilder(services);
        app.UseLicenseGate();
        app.Run(_ => { reached = true; return Task.CompletedTask; });
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Path = path; ctx.Request.Method = method;
        ctx.Response.Body = new MemoryStream();
        await app.Build()(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync(), reached);
    }

    [Theory]
    [InlineData("/api/runs")] [InlineData("/api/settings")] [InlineData("/api/users")] [InlineData("/api/ai/status")]
    [InlineData("/api/schedules")] [InlineData("/api/masterdata/items")] [InlineData("/api/admin")] [InlineData("/api/admin/licensefoo")]
    [InlineData("/API/Runs")] [InlineData("/api")]
    public async Task Unlicensed_ApiCalls_Get402_WithTheLicenceRequiredBody(string path)
    {
        var (status, body, reached) = await Call(false, path);
        Assert.Equal(402, status); Assert.False(reached);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("licenseRequired").GetBoolean());
        Assert.Equal("NotLicensed", doc.RootElement.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
    }

    [Theory]
    [InlineData("/api/auth/status", "GET")] [InlineData("/api/auth/login", "POST")] [InlineData("/api/auth/logout", "POST")] [InlineData("/api/auth/setup", "POST")]
    [InlineData("/api/admin/license", "GET")] [InlineData("/api/admin/license", "POST")]
    [InlineData("/api/admin/backup/schedule", "GET")] [InlineData("/api/admin/backup/run", "POST")] [InlineData("/api/admin/backup/download", "GET")]
    [InlineData("/", "GET")] [InlineData("/index.html", "GET")] [InlineData("/assets/app.js", "GET")] [InlineData("/settings", "GET")]
    public async Task Unlicensed_AllowList_PassesThrough(string path, string method)
    {
        var (status, _, reached) = await Call(false, path, method);
        Assert.NotEqual(402, status); Assert.True(reached);
    }

    [Fact] public async Task Licensed_EverythingPasses()
    {
        foreach (var p in new[] { "/api/runs", "/api/settings", "/api/admin/license" })
            Assert.True((await Call(true, p)).Reached);
    }

    // ---------------------------------------------------------------- who may do what (endpoint metadata)

    private static IReadOnlyList<RouteEndpoint> MapAdminEndpoints()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Services.AddSingleton<ILicenseService>(new FakeLicense(true));     // as in Program.cs, so parameters bind from DI
        builder.Services.AddSingleton(new BackupPaths("x", "y"));
        builder.Services.AddSingleton<BackupRunner>();
        var app = builder.Build();
        var admin = app.MapGroup("/api/admin");
        admin.MapLicense();
        admin.MapBackup();
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    private static string Method(RouteEndpoint e) => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single();

    [Fact] public void LicenseSave_IsAdminOnly_LicenseRead_IsOpen()
    {
        var eps = MapAdminEndpoints();
        var save = eps.Single(e => e.RoutePattern.RawText == "/api/admin/license" && Method(e) == "POST");
        var read = eps.Single(e => e.RoutePattern.RawText == "/api/admin/license" && Method(e) == "GET");
        Assert.Contains(save.Metadata.GetOrderedMetadata<IAuthorizeData>(), a => a.Policy == "Admin");
        Assert.NotNull(read.Metadata.GetMetadata<IAllowAnonymous>());
    }

    [Fact] public void EveryBackupEndpoint_IsAdminOnly_AndThereIsNoRestore()
    {
        var backup = MapAdminEndpoints().Where(e => e.RoutePattern.RawText!.StartsWith("/api/admin/backup")).ToList();
        Assert.Equal(4, backup.Count);   // download, schedule get, schedule save, run
        foreach (var e in backup)
        {
            Assert.Contains(e.Metadata.GetOrderedMetadata<IAuthorizeData>(), a => a.Policy == "Admin");
            Assert.Null(e.Metadata.GetMetadata<IAllowAnonymous>());
        }
        Assert.DoesNotContain(backup, e => e.RoutePattern.RawText!.Contains("restore", StringComparison.OrdinalIgnoreCase));
    }
}
