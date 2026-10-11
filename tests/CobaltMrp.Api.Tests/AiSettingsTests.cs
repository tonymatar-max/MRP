using CobaltMrp.Api.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CobaltMrp.Api.Tests;

public class AiSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cobalt-ai-test-" + Guid.NewGuid().ToString("N"));
    private readonly IDataProtectionProvider _dp = new EphemeralDataProtectionProvider();
    public AiSettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SettingsService Svc() => new(_dir, new SecretProtector(_dp), NullLogger<SettingsService>.Instance);
    private const string Key = "sk-or-SECRET-VALUE-9876";

    [Fact]
    public void Defaults_AreOff_AndEmpty()
    {
        var a = new AppSettings().Ai;
        Assert.False(a.Enabled);
        Assert.Equal("", a.ApiKey);
        Assert.Equal("anthropic/claude-opus-5", a.Model);
        Assert.False(a.IsConfigured);
    }

    [Fact]
    public void Key_IsMasked_ProtectedAtRest_AndBlankKeepsIt()
    {
        var s = Svc();
        var inc = new AppSettings(); inc.Ai.Enabled = true; inc.Ai.ApiKey = "  " + Key + "  ";
        s.Save(inc);
        Assert.Equal(Key, s.Get().Ai.ApiKey);                                   // trimmed, kept in memory only
        var masked = SettingsService.Masked(s.Get());
        Assert.Equal("", masked.Ai.ApiKey);
        Assert.True(masked.Ai.ApiKeySet);
        Assert.DoesNotContain(Key, File.ReadAllText(Path.Combine(_dir, "settings.json")));

        var blank = new AppSettings(); blank.Ai.Enabled = true; blank.Ai.ApiKey = "";
        s.Save(blank);
        Assert.Equal(Key, s.Get().Ai.ApiKey);                                   // blank keeps the stored key

        Assert.Equal(Key, Svc().Get().Ai.ApiKey);                               // survives a restart (decrypts)
    }

    [Fact]
    public void Normalisation_ClampsMaxTokens_DefaultsModel()
    {
        var s = Svc();
        var inc = new AppSettings(); inc.Ai.MaxTokens = 999999; inc.Ai.Model = "   ";
        var r = s.Save(inc).Ai;
        Assert.Equal(8192, r.MaxTokens);
        Assert.Equal(AiSettings.DefaultModel, r.Model);
        inc.Ai.MaxTokens = 1; Assert.Equal(64, s.Save(inc).Ai.MaxTokens);
        inc.Ai.MaxTokens = 0; Assert.Equal(1024, s.Save(inc).Ai.MaxTokens);
        inc.Ai.Model = new string('m', 900); Assert.Equal(500, s.Save(inc).Ai.Model.Length);
        inc.Ai.Model = "a/one,\n b/two ;a/ONE, c/three"; Assert.Equal("a/one, b/two, c/three", s.Save(inc).Ai.Model);   // fallback list: trimmed, de-duplicated
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var s = Svc();
        var inc = new AppSettings(); inc.Ai.ApiKey = Key; s.Save(inc);
        var c = s.Get(); c.Ai.ApiKey = "changed"; c.Ai.Enabled = true;
        Assert.Equal(Key, s.Get().Ai.ApiKey);
        Assert.False(s.Get().Ai.Enabled);
    }

    [Fact]
    public void Preview_UsesTheStoredKey_WhenBlank()
    {
        var s = Svc();
        var inc = new AppSettings(); inc.Ai.ApiKey = Key; s.Save(inc);
        var test = new AppSettings(); test.Ai.Model = "other/model";
        var p = s.Preview(test);
        Assert.Equal(Key, p.Ai.ApiKey);
        Assert.Equal("other/model", p.Ai.Model);
    }
}
