// Cobalt licence verification (Cobalt licence + backup standard, adapted for Cobalt MRP).
//
// The product only VERIFIES a licence, with an embedded RSA PUBLIC key. The private key lives only in the
// licence generator (Cobalt License Manager, C:\Claude\License) and is never shipped, committed or copied.
//
// Licence key format (made by the generator, "ICL1" = schema 1):
//     ICL1.<payload>.<signature>
//     payload   = base64url( JSON { SchemaVersion, ProductCode, HardwareKey, ExpiresOn, MaxSeats?, Customer? } )
//     signature = base64url( RSA-SHA256-PKCS1( UTF8(payload-part-as-text) ) )     <- signs the base64url TEXT
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CobaltMrp.Api.Licensing;

public static class LicenseDefaults
{
    public const string ProductCode = "CobaltMRP";
    public const string FileName = "license.json";
    /// <summary>Environment variable that swaps the verification key. Honoured ONLY in the Development environment.</summary>
    public const string TestKeyVariable = "COBALT_LICENSE_TEST_PUBLIC_KEY";

    // Public key (SubjectPublicKeyInfo, base64) the Cobalt generator's signing key verifies against. It is a
    // PUBLIC key and safe to commit. If the signing key is rotated, change it here AND in every Cobalt product.
    public const string PublicKeySpki =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAxKynkLCkZlgpb3uAbTlbzm+1/BMNDC7/z2SvAUC//qgI3N050lKLP2+STcymBP2gLoPsyEduUtOMv8c5lac37euWlkp5/uJFlGSLLbyzawR4ZXbwNcHPp64EvZmCI1dfakC/q7gldlIiF52OU3gia6J94FBWu0TH7bx+ehfY/Gex49ZbvxljQe60uCaeeSikiPlKNbReMfeBoHl3agcMR7vj/jDGdWGog2ps0FtvPfU3OYixJ1Zaczq1hSphFtpM3NrNj2aL1evq/DI8SLNlmMyIKg64Z388GHTbeq4ZHMHeLqf8mCvClJj0Gcls7Co8IvgSRzkh6EgfoY5RHDeSSQIDAQAB";

    /// <summary>
    /// The key the verifier uses. Production ALWAYS gets the embedded constant: the override variable is read
    /// only when the host environment is Development, and never from settings.json or appsettings.
    /// </summary>
    public static string ResolvePublicKey(bool isDevelopment, string? overrideFromEnvironment) =>
        isDevelopment && !string.IsNullOrWhiteSpace(overrideFromEnvironment) ? overrideFromEnvironment.Trim() : PublicKeySpki;
}

/// <summary>Why a licence is not valid (or Valid).</summary>
public enum LicenseState { Valid, NotLicensed, Malformed, BadSignature, WrongProduct, WrongMachine, Expired }

/// <summary>What is stored in license.json (the key only; everything else is re-derived from it).</summary>
public sealed class StoredLicense
{
    public string LicenseKey { get; set; } = "";
}

public sealed class LicensePayload
{
    public string SchemaVersion { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public string HardwareKey { get; set; } = "";
    public DateTime ExpiresOn { get; set; }
    public int? MaxSeats { get; set; }
    /// <summary>Optional customer name if the generator embeds one.</summary>
    public string? Customer { get; set; }
}

/// <summary>The verified picture of this machine's licence. Never contains the key itself.</summary>
public sealed record LicenseStatus(
    string ProductCode, string HardwareKey, LicenseState State, bool IsValid, bool LicenseKeySet,
    DateTime? ExpiresOn, string? Customer, int? MaxSeats, string Message)
{
    public static string Describe(LicenseState s, DateTime? expires) => s switch
    {
        LicenseState.Valid => "Licensed.",
        LicenseState.NotLicensed => "No licence key has been entered for this server yet.",
        LicenseState.Malformed => "That is not a Cobalt licence key. Paste the whole key exactly as generated (it starts with ICL1).",
        LicenseState.BadSignature => "The licence key is not genuine (its signature does not verify). Ask for a new key.",
        LicenseState.WrongProduct => $"This key is for a different product. Ask for a {LicenseDefaults.ProductCode} key.",
        LicenseState.WrongMachine => "This key was issued for a different server or install folder. Send the Hardware Key shown on this page and ask for a new key.",
        LicenseState.Expired => expires is { } e ? $"The licence expired on {e:yyyy-MM-dd}. Ask for a renewed key." : "The licence has expired.",
        _ => "Not licensed.",
    };
}

public sealed record LicenseSaveResult(bool Saved, LicenseStatus Status);

public interface ILicenseService
{
    LicenseStatus Load();
    /// <summary>Verifies and stores a key. An invalid key is never stored (so it can never replace a good one).</summary>
    LicenseSaveResult Save(string licenseKey);
    bool IsValid();
}

public sealed class LicenseService : ILicenseService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null, PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly TimeSpan IsValidCacheTtl = TimeSpan.FromMinutes(5);

    private readonly string _contentRoot;
    private readonly string _licensePath;
    private readonly ILogger _logger;
    private readonly string _publicKeySpki;
    private readonly object _cacheLock = new();
    private bool _cachedIsValid;
    private DateTime _cachedAt = DateTime.MinValue;
    private LicenseState? _lastLoggedState;

    /// <param name="contentRootPath">The app folder; with the machine name it makes the hardware key.</param>
    /// <param name="dataDir">Where license.json lives: the data folder that a republish never touches.</param>
    /// <param name="publicKeySpki">Tests inject a throwaway key; production passes <see cref="LicenseDefaults.ResolvePublicKey"/>.</param>
    public LicenseService(string contentRootPath, string dataDir, ILogger logger, string? publicKeySpki = null)
    {
        _contentRoot = contentRootPath;
        _licensePath = Path.Combine(dataDir, LicenseDefaults.FileName);
        _logger = logger;
        _publicKeySpki = publicKeySpki ?? LicenseDefaults.PublicKeySpki;
    }

    public string LicensePath => _licensePath;

    public LicenseStatus Load() => Evaluate(ReadStoredKey());

    public LicenseSaveResult Save(string licenseKey)
    {
        var key = (licenseKey ?? "").Trim();
        var status = Evaluate(key);
        if (!status.IsValid) return new LicenseSaveResult(false, status with { LicenseKeySet = false });
        Directory.CreateDirectory(Path.GetDirectoryName(_licensePath)!);
        var tmp = _licensePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new StoredLicense { LicenseKey = key }, JsonOptions));
        File.Move(tmp, _licensePath, overwrite: true);
        lock (_cacheLock) { _cachedAt = DateTime.MinValue; }   // a new key takes effect now, not in 5 minutes
        _logger.LogInformation("A new Cobalt MRP licence key was saved (expires {Expiry:yyyy-MM-dd}).", status.ExpiresOn);
        return new LicenseSaveResult(true, status);
    }

    /// <summary>Cached for 5 minutes: this runs on every API request.</summary>
    public bool IsValid()
    {
        lock (_cacheLock)
        {
            if (DateTime.UtcNow - _cachedAt < IsValidCacheTtl) return _cachedIsValid;
        }
        var status = Load();
        lock (_cacheLock) { _cachedIsValid = status.IsValid; _cachedAt = DateTime.UtcNow; }
        if (_lastLoggedState != status.State)
        {
            _lastLoggedState = status.State;
            if (status.IsValid) _logger.LogInformation("Cobalt MRP licence is valid (expires {Expiry:yyyy-MM-dd}).", status.ExpiresOn);
            else _logger.LogWarning("Cobalt MRP is not licensed ({State}). Enter a licence key under Settings > Licence.", status.State);
        }
        return status.IsValid;
    }

    private string ReadStoredKey()
    {
        if (!File.Exists(_licensePath)) return "";
        try
        {
            return JsonSerializer.Deserialize<StoredLicense>(File.ReadAllText(_licensePath), JsonOptions)?.LicenseKey ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "License file '{Path}' is corrupted or unreadable; treating as unlicensed.", _licensePath);
            return "";
        }
    }

    public string HardwareKey => ComputeHardwareKey(Environment.MachineName, NormalizeRoot(_contentRoot));

    /// <summary>The same folder always gives the same text, with or without a trailing separator.</summary>
    public static string NormalizeRoot(string root) => root.TrimEnd('\\', '/');

    /// <summary>
    /// SHA-256 of "MachineName|ContentRootPath", first 32 hex chars. GOTCHA: because the install folder is part
    /// of the key, MOVING OR RENAMING THE INSTALL FOLDER (or renaming the machine) INVALIDATES THE LICENCE and
    /// a new key must be issued for the new hardware key.
    /// </summary>
    public static string ComputeHardwareKey(string machineName, string contentRootPath)
    {
        var raw = $"{machineName}|{contentRootPath}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32];
    }

    private LicenseStatus Evaluate(string licenseKey)
    {
        var hw = HardwareKey;
        if (string.IsNullOrWhiteSpace(licenseKey))
            return new LicenseStatus(LicenseDefaults.ProductCode, hw, LicenseState.NotLicensed, false, false, null, null, null,
                LicenseStatus.Describe(LicenseState.NotLicensed, null));

        LicenseStatus Fail(LicenseState s, LicensePayload? p = null) =>
            new(LicenseDefaults.ProductCode, hw, s, false, true, p?.ExpiresOn.Date, p?.Customer, p?.MaxSeats, LicenseStatus.Describe(s, p?.ExpiresOn.Date));

        if (!TryReadLicenseToken(licenseKey, out var payload, out var payloadPart, out var signature)) return Fail(LicenseState.Malformed);
        // Signature first: nothing inside the payload is trusted (or shown) until it verifies.
        if (!VerifyLicenseSignature(payloadPart, signature)) return Fail(LicenseState.BadSignature);
        if (!string.Equals(payload.ProductCode, LicenseDefaults.ProductCode, StringComparison.OrdinalIgnoreCase)) return Fail(LicenseState.WrongProduct);
        if (!string.Equals(payload.HardwareKey, hw, StringComparison.OrdinalIgnoreCase)) return Fail(LicenseState.WrongMachine);
        if (payload.ExpiresOn.Date < DateTime.UtcNow.Date) return Fail(LicenseState.Expired, payload);   // expiry day itself is still valid (UTC date)
        return new LicenseStatus(LicenseDefaults.ProductCode, hw, LicenseState.Valid, true, true, payload.ExpiresOn.Date, payload.Customer, payload.MaxSeats,
            LicenseStatus.Describe(LicenseState.Valid, null));
    }

    private bool VerifyLicenseSignature(string payloadPart, string signature)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(_publicKeySpki), out _);
            return rsa.VerifyData(Encoding.UTF8.GetBytes(payloadPart), Base64UrlDecode(signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch { return false; }
    }

    private static bool TryReadLicenseToken(string? licenseKey, out LicensePayload payload, out string payloadPart, out string signature)
    {
        payload = new LicensePayload();
        payloadPart = "";
        signature = "";
        if (string.IsNullOrWhiteSpace(licenseKey)) return false;
        var parts = licenseKey.Trim().Split('.');
        if (parts.Length != 3 || !parts[0].Equals("ICL1", StringComparison.Ordinal)) return false;
        try
        {
            payloadPart = parts[1];
            signature = parts[2];
            var json = Encoding.UTF8.GetString(Base64UrlDecode(payloadPart));
            payload = JsonSerializer.Deserialize<LicensePayload>(json, JsonOptions) ?? new LicensePayload();
            return !string.IsNullOrWhiteSpace(payload.ProductCode) && !string.IsNullOrWhiteSpace(payload.HardwareKey) && payload.ExpiresOn != default;
        }
        catch { return false; }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
