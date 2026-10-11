using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace CobaltMrp.Api.Settings;

/// <summary>Encrypts secrets at rest (ASP.NET DataProtection; keys live under data/keys).</summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _p = provider.CreateProtector("CobaltMrp.Secrets.v1");
    public string Protect(string plain) => string.IsNullOrEmpty(plain) ? "" : _p.Protect(plain);

    /// <summary>Returns "" when the value cannot be decrypted (e.g. the key ring was lost).</summary>
    public string Unprotect(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try { return _p.Unprotect(cipher); } catch { return ""; }
    }
}

/// <summary>
/// Loads and saves <c>data/settings.json</c>. Secrets are written protected and are held in plain text
/// only in memory. Changes apply immediately: no restart is needed after saving on the Settings screen.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly SecretProtector _secrets;
    private readonly ILogger<SettingsService> _log;
    private readonly object _lock = new();
    private AppSettings _current;

    public SettingsService(string dataDir, SecretProtector secrets, ILogger<SettingsService> log)
    {
        _path = Path.Combine(dataDir, "settings.json");
        _secrets = secrets;
        _log = log;
        _current = Load();
    }

    /// <summary>A snapshot copy; safe to read and modify without affecting the live settings.</summary>
    public AppSettings Get() { lock (_lock) return _current.Clone(); }

    /// <summary>Apply an edit from the UI. Empty passwords keep the stored value.</summary>
    public AppSettings Save(AppSettings incoming)
    {
        lock (_lock)
        {
            var merged = Merge(_current, incoming);
            Write(merged);
            _current = merged;
            return _current.Clone();
        }
    }

    /// <summary>Overlay UI values on the stored settings without saving (used by the Test buttons).</summary>
    public AppSettings Preview(AppSettings incoming) { lock (_lock) return Merge(_current, incoming); }

    /// <summary>For GET: secrets blanked, with the *Set flags telling the UI whether one is stored.</summary>
    public static AppSettings Masked(AppSettings s)
    {
        var c = s.Clone();
        c.CobaltDb.PasswordSet = !string.IsNullOrEmpty(c.CobaltDb.Password); c.CobaltDb.Password = "";
        c.CobaltDb.ConnectionStringOverride = DbSettings.MaskOverride(c.CobaltDb.ConnectionStringOverride);
        c.B1Db.PasswordSet = !string.IsNullOrEmpty(c.B1Db.Password); c.B1Db.Password = "";
        c.B1Db.ConnectionStringOverride = DbSettings.MaskOverride(c.B1Db.ConnectionStringOverride);
        c.ServiceLayer.PasswordSet = !string.IsNullOrEmpty(c.ServiceLayer.Password); c.ServiceLayer.Password = "";
        c.Ai.ApiKeySet = !string.IsNullOrEmpty(c.Ai.ApiKey); c.Ai.ApiKey = "";
        return c;
    }

    private static AppSettings Merge(AppSettings stored, AppSettings incoming)
    {
        var m = incoming.Clone();
        m.CobaltDb.ConnectionStringOverride = DbSettings.UnmaskOverride(m.CobaltDb.ConnectionStringOverride, stored.CobaltDb.ConnectionStringOverride);
        m.B1Db.ConnectionStringOverride = DbSettings.UnmaskOverride(m.B1Db.ConnectionStringOverride, stored.B1Db.ConnectionStringOverride);
        if (string.IsNullOrEmpty(m.CobaltDb.Password)) m.CobaltDb.Password = stored.CobaltDb.Password;
        if (string.IsNullOrEmpty(m.B1Db.Password)) m.B1Db.Password = stored.B1Db.Password;
        if (string.IsNullOrEmpty(m.ServiceLayer.Password)) m.ServiceLayer.Password = stored.ServiceLayer.Password;
        m.Ai ??= new AiSettings();
        m.Ai.ApiKey = string.IsNullOrWhiteSpace(m.Ai.ApiKey) ? stored.Ai.ApiKey : m.Ai.ApiKey.Trim();
        // One or more model ids, comma or newline separated: the first is the primary, the rest are fallbacks.
        var modelIds = (m.Ai.Model ?? "").Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        m.Ai.Model = modelIds.Count == 0 ? AiSettings.DefaultModel : string.Join(", ", modelIds);
        if (m.Ai.Model.Length > 500) m.Ai.Model = m.Ai.Model[..500];
        m.Ai.MaxTokens = m.Ai.MaxTokens <= 0 ? AiSettings.DefaultMaxTokens : Math.Clamp(m.Ai.MaxTokens, 64, 8192);
        m.ServiceLayer.ItemVendors = m.ServiceLayer.ItemVendors
            .Where(v => !string.IsNullOrWhiteSpace(v.ItemCode) && !string.IsNullOrWhiteSpace(v.CardCode)).ToList();
        m.Planning.HorizonDays = Math.Clamp(m.Planning.HorizonDays, 1, 1095);
        m.Planning.BucketDays = Math.Clamp(m.Planning.BucketDays, 1, 90);
        if (m.Planning.HoursPerUnitPerDay <= 0) m.Planning.HoursPerUnitPerDay = 8m;
        m.Planning.ForecastLookbackDays = Math.Max(0, m.Planning.ForecastLookbackDays);
        if (m.Planning.DefaultSource is not ("mock" or "b1-sql")) m.Planning.DefaultSource = "mock";
        if (m.Planning.ForecastConsumption is not ("Forward" or "None")) m.Planning.ForecastConsumption = "Forward";
        if (m.Planning.LeadTimeMode is not ("FirstAvailable" or "Longest")) m.Planning.LeadTimeMode = "FirstAvailable";
        m.Planning.DefaultLeadTimeDays = Math.Clamp(m.Planning.DefaultLeadTimeDays, 0, 3650);
        if (m.Planning.DefaultLevel is not ("Warehouse" or "Company")) m.Planning.DefaultLevel = "Warehouse";
        if (m.Planning.PurchaseDocument is not ("Order" or "Request")) m.Planning.PurchaseDocument = "Order";
        m.Planning.TransferLeadTimeDays = Math.Clamp(m.Planning.TransferLeadTimeDays, 0, 90);
        // Priority: valid names, once each, and always all three (missing ones appended in the default order).
        var pri = new List<string>();
        foreach (var n in m.Planning.LeadTimePriority ?? new())
            if (Enum.TryParse<CobaltMrp.Domain.LeadTimeSourceKind>(n, true, out var k) && !pri.Contains(k.ToString())) pri.Add(k.ToString());
        foreach (var k in new[] { "Supplier", "Item", "ItemGroup" }) if (!pri.Contains(k)) pri.Add(k);
        m.Planning.LeadTimePriority = pri;
        // Normalise the calendar inputs so a bad edit cannot break planning.
        var days = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        m.Planning.WorkingDays = (m.Planning.WorkingDays ?? new())
            .Select(d => days.FirstOrDefault(x => d != null && d.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
            .Where(d => d != null).Select(d => d!).Distinct().ToList();
        m.Planning.NonWorkingDates = (m.Planning.NonWorkingDates ?? new())
            .GroupBy(n => n.Date).Select(g => g.First()).OrderBy(n => n.Date).ToList();
        m.Planning.Resources = (m.Planning.Resources ?? new())
            .Where(r => !string.IsNullOrWhiteSpace(r.Code))
            .GroupBy(r => r.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(r => new ResourceSetting
            {
                Code = r.Code.Trim(),
                HoursPerDay = Math.Clamp(r.HoursPerDay, 0m, 24m),
                EfficiencyPercent = Math.Clamp(r.EfficiencyPercent, 1m, 200m),
                SetupHoursPerOrder = Math.Max(0m, r.SetupHoursPerOrder),
            }).ToList();
        return m;
    }

    private AppSettings Load()
    {
        if (!File.Exists(_path)) return new AppSettings();
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json) ?? new AppSettings();
            s.CobaltDb.Password = _secrets.Unprotect(s.CobaltDb.Password);
            s.B1Db.Password = _secrets.Unprotect(s.B1Db.Password);
            s.ServiceLayer.Password = _secrets.Unprotect(s.ServiceLayer.Password);
            s.Ai ??= new AiSettings();
            s.Ai.ApiKey = _secrets.Unprotect(s.Ai.ApiKey);
            s.CobaltDb.ConnectionStringOverride = _secrets.Unprotect(s.CobaltDb.ConnectionStringOverride);
            s.B1Db.ConnectionStringOverride = _secrets.Unprotect(s.B1Db.ConnectionStringOverride);
            return s;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not read {Path}; starting with defaults.", _path);
            return new AppSettings();
        }
    }

    private void Write(AppSettings s)
    {
        var onDisk = s.Clone();
        onDisk.CobaltDb.PasswordSet = onDisk.B1Db.PasswordSet = onDisk.ServiceLayer.PasswordSet = onDisk.Ai.ApiKeySet = false;
        onDisk.CobaltDb.Password = _secrets.Protect(s.CobaltDb.Password);
        onDisk.B1Db.Password = _secrets.Protect(s.B1Db.Password);
        onDisk.ServiceLayer.Password = _secrets.Protect(s.ServiceLayer.Password);
        onDisk.Ai.ApiKey = _secrets.Protect(s.Ai.ApiKey);
        // A raw connection string may embed a password, so it is protected whole.
        onDisk.CobaltDb.ConnectionStringOverride = _secrets.Protect(s.CobaltDb.ConnectionStringOverride);
        onDisk.B1Db.ConnectionStringOverride = _secrets.Protect(s.B1Db.ConnectionStringOverride);

        // Write-then-replace so a crash mid-save can never leave a truncated settings file.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(onDisk, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
