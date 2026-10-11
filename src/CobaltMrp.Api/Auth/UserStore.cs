using System.Security.Cryptography;
using System.Text.Json;

namespace CobaltMrp.Api.Auth;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Planner = "Planner";
    public static bool IsValid(string? r) => r is Admin or Planner;
}

public sealed class UserRecord
{
    public string Username { get; set; } = "";
    public string Role { get; set; } = Roles.Planner;
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
    public int Iterations { get; set; }
}

/// <summary>
/// File-backed users (<c>data/users.json</c>) with PBKDF2-SHA256 password hashes. Small by design: a
/// planning tool has a handful of users, and this keeps sign-in working before any database is configured.
/// </summary>
public sealed class UserStore
{
    private const int Iterations = 210_000;
    public const int MinPasswordLength = 8;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private List<UserRecord> _users;

    // Throttle: after 5 failures within 5 minutes a username is locked out for the rest of that window.
    private readonly Dictionary<string, List<DateTime>> _failures = new(StringComparer.OrdinalIgnoreCase);

    public UserStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "users.json");
        _users = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<UserRecord>>(File.ReadAllText(_path), Json) ?? new()
            : new();
    }

    public bool HasUsers { get { lock (_lock) return _users.Count > 0; } }

    public IReadOnlyList<(string Username, string Role)> List()
    {
        lock (_lock) return _users.OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(u => (u.Username, u.Role)).ToList();
    }

    /// <summary>Returns an error message, or null on success.</summary>
    public string? Create(string username, string password, string role)
    {
        username = (username ?? "").Trim();
        if (username.Length < 2) return "Username must be at least 2 characters.";
        if ((password ?? "").Length < MinPasswordLength) return $"Password must be at least {MinPasswordLength} characters.";
        if (!Roles.IsValid(role)) return "Role must be Admin or Planner.";
        lock (_lock)
        {
            if (_users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
                return "That username already exists.";
            _users.Add(Hash(username, password!, role));
            Save();
        }
        return null;
    }

    public string? SetPassword(string username, string password)
    {
        if ((password ?? "").Length < MinPasswordLength) return $"Password must be at least {MinPasswordLength} characters.";
        lock (_lock)
        {
            var i = _users.FindIndex(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return "User not found.";
            _users[i] = Hash(_users[i].Username, password!, _users[i].Role);
            Save();
        }
        return null;
    }

    public string? Delete(string username, string actingUser)
    {
        lock (_lock)
        {
            var u = _users.FirstOrDefault(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
            if (u is null) return "User not found.";
            if (string.Equals(u.Username, actingUser, StringComparison.OrdinalIgnoreCase)) return "You cannot delete yourself.";
            if (u.Role == Roles.Admin && _users.Count(x => x.Role == Roles.Admin) <= 1) return "You cannot delete the last administrator.";
            _users.Remove(u);
            Save();
        }
        return null;
    }

    /// <summary>Validate credentials. <paramref name="lockedOut"/> is true when throttled.</summary>
    public UserRecord? Validate(string username, string password, out bool lockedOut)
    {
        lockedOut = false;
        lock (_lock)
        {
            var window = DateTime.UtcNow.AddMinutes(-5);
            if (_failures.TryGetValue(username ?? "", out var f))
            {
                f.RemoveAll(t => t < window);
                if (f.Count >= 5) { lockedOut = true; return null; }
            }

            var u = _users.FirstOrDefault(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
            // Always do the hash work so timing does not reveal whether the user exists.
            var salt = u is null ? new byte[16] : Convert.FromBase64String(u.Salt);
            var computed = Pbkdf2(password ?? "", salt, u?.Iterations ?? Iterations);
            var ok = u is not null && CryptographicOperations.FixedTimeEquals(computed, Convert.FromBase64String(u.Hash));
            if (ok) { _failures.Remove(username!); return u; }

            if (!_failures.TryGetValue(username ?? "", out f)) _failures[username ?? ""] = f = new();
            f.Add(DateTime.UtcNow);
            return null;
        }
    }

    private static UserRecord Hash(string username, string password, string role)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new UserRecord
        {
            Username = username, Role = role, Iterations = Iterations,
            Salt = Convert.ToBase64String(salt), Hash = Convert.ToBase64String(Pbkdf2(password, salt, Iterations)),
        };
    }

    private static byte[] Pbkdf2(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_users, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
