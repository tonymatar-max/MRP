using System.IO.Compression;
using System.Text.Json;

namespace CobaltMrp.Api.Backup;

/// <summary>Where the app lives (for the wwwroot safety check) and where its state files live (what gets backed up).</summary>
public sealed record BackupPaths(string ContentRoot, string DataDir);

/// <summary>
/// The files a configuration backup contains, and the one routine that writes them into a zip.
/// Shared by the manual "Download backup" button and the scheduled backup so the two can never drift apart.
/// Everything is relative to the data folder (the one a republish never touches).
/// What is NOT here on purpose: the planned runs, forecasts and master-data overrides. They live in the SQL
/// database (CobaltMrp DB) and need a SQL Server backup.
/// </summary>
public static class BackupCatalog
{
    // LIST EVERY STATE/CONFIG FILE THE APP WRITES UNDER ITS DATA FOLDER. A restored backup silently loses state
    // for any file that is missing here.
    public static readonly string[] FileNames =
    [
        "settings.json",            // all configuration; secrets inside are DataProtection-encrypted
        "users.json",               // users + password hashes
        "automation-state.json",    // what each schedule did last (so a restore does not repeat a day's run)
        "backup-schedule.json",     // this very schedule
        "license.json",             // the licence key
    ];

    /// <summary>
    /// Files with no fixed name, as "folder/pattern" relative to the data folder (forward slash). The DataProtection key ring:
    /// WITHOUT IT the encrypted secrets in settings.json (SQL, Service Layer, SMTP and AI passwords) cannot be decrypted
    /// after a restore. Restore (manual today) must allow exactly the same set: see <see cref="IsAllowedEntry"/>.
    /// </summary>
    public static readonly string[] DynamicFilePatterns = ["keys/*"];

    /// <summary>True for an archive entry name a restore may write: a catalog file, or a file matching a dynamic pattern.</summary>
    public static bool IsAllowedEntry(string entryName)
    {
        if (string.IsNullOrEmpty(entryName) || entryName.Contains('\\') || entryName.Contains("..") || Path.IsPathRooted(entryName)) return false;
        if (FileNames.Contains(entryName, StringComparer.Ordinal)) return true;
        foreach (var pattern in DynamicFilePatterns)
        {
            var slash = pattern.LastIndexOf('/');
            var dir = slash < 0 ? "" : pattern[..slash];
            var file = pattern[(slash + 1)..];
            var name = dir.Length == 0 ? entryName : entryName.StartsWith(dir + "/", StringComparison.Ordinal) ? entryName[(dir.Length + 1)..] : null;
            if (name is null || name.Contains('/')) continue;
            if (file == "*" ? name.Length > 0 : name.Equals(file, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Every file that would go into a backup right now, as (entry name, full path).</summary>
    public static List<(string Entry, string Path)> Enumerate(string dataDir)
    {
        var list = new List<(string, string)>();
        foreach (var name in FileNames)
        {
            var path = Path.Combine(dataDir, name);
            if (File.Exists(path)) list.Add((name, path));
        }
        foreach (var pattern in DynamicFilePatterns)
        {
            var slash = pattern.LastIndexOf('/');
            var dir = slash < 0 ? "" : pattern[..slash];
            var folder = dir.Length == 0 ? dataDir : Path.Combine(dataDir, dir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(folder)) continue;
            foreach (var f in Directory.GetFiles(folder, pattern[(slash + 1)..]).OrderBy(x => x, StringComparer.Ordinal))
                if (!f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    list.Add(((dir.Length == 0 ? "" : dir + "/") + Path.GetFileName(f), f));
        }
        return list;
    }

    /// <summary>Writes the manifest plus every existing state file into <paramref name="stream"/>.</summary>
    public static void WriteZip(Stream stream, string dataDir, string version)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        // Manifest: provenance for whoever restores it. Lists the members so a missing one is visible.
        var files = Enumerate(dataDir);
        var manifest = new
        {
            CreatedAt = DateTime.UtcNow.ToString("o"),
            MachineName = Environment.MachineName,
            Product = "Cobalt MRP",
            Version = version,
            Files = files.Select(f => f.Entry).ToArray(),
            Note = "Planned runs, forecasts and master-data overrides are in the SQL database and are NOT in this backup.",
        };
        var manifestEntry = zip.CreateEntry("backup-info.json", CompressionLevel.Fastest);
        using (var w = new StreamWriter(manifestEntry.Open()))
            w.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        foreach (var (entry, path) in files) AddFile(zip, path, entry);
    }

    // The live service rewrites these files; open with shared access so a backup taken mid-write
    // reads whatever is on disk instead of failing on a sharing violation.
    private static void AddFile(ZipArchive zip, string path, string entryName)
    {
        try
        {
            using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var dst = entry.Open();
            src.CopyTo(dst);
        }
        catch (FileNotFoundException) { /* removed between listing and reading (e.g. a rotated key): skip */ }
    }
}

public sealed class BackupScheduleSettings
{
    public bool Enabled { get; set; }
    /// <summary>"daily" or "weekly".</summary>
    public string Frequency { get; set; } = "daily";
    /// <summary>Local server time, HH:mm (24 hour).</summary>
    public string Time { get; set; } = "02:00";
    /// <summary>For weekly: 0 = Sunday ... 6 = Saturday (System.DayOfWeek).</summary>
    public int DayOfWeek { get; set; }
    /// <summary>Empty = the default folder under ProgramData.</summary>
    public string Folder { get; set; } = "";
    /// <summary>How many scheduled backups to keep; older ones are deleted.</summary>
    public int KeepLast { get; set; } = 14;
}

public sealed class BackupFileInfo
{
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class BackupRunResult
{
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public string Trigger { get; set; } = "";
    public bool Success { get; set; }
    public string File { get; set; } = "";
    public long SizeBytes { get; set; }
    public int Pruned { get; set; }
    public string? Error { get; set; }
}

public static class BackupScheduleStore
{
    public const string FileName = "backup-schedule.json";
    public const string BackupPrefix = "cobalt-mrp-backup-auto-";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly object FileLock = new();

    public static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Cobalt", "MRP", "Backups", "Scheduled");

    public static BackupScheduleSettings Load(string dataDir)
    {
        var path = Path.Combine(dataDir, FileName);
        lock (FileLock)
        {
            if (!File.Exists(path)) return new BackupScheduleSettings();
            try
            {
                return Normalize(JsonSerializer.Deserialize<BackupScheduleSettings>(File.ReadAllText(path), Options)
                                 ?? new BackupScheduleSettings());
            }
            catch { return new BackupScheduleSettings(); }
        }
    }

    public static void Save(string dataDir, BackupScheduleSettings settings)
    {
        var path = Path.Combine(dataDir, FileName);
        lock (FileLock)
        {
            Directory.CreateDirectory(dataDir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Normalize(settings), Options));
            File.Move(tmp, path, overwrite: true);
        }
    }

    public static BackupScheduleSettings Normalize(BackupScheduleSettings s)
    {
        s.Frequency = string.Equals(s.Frequency, "weekly", StringComparison.OrdinalIgnoreCase) ? "weekly" : "daily";
        s.Time = TimeSpan.TryParseExact((s.Time ?? "").Trim(), @"hh\:mm", null, out var t) && t < TimeSpan.FromDays(1)
            ? t.ToString(@"hh\:mm")
            : "02:00";
        s.DayOfWeek = Math.Clamp(s.DayOfWeek, 0, 6);
        s.KeepLast = Math.Clamp(s.KeepLast, 1, 365);
        s.Folder = (s.Folder ?? "").Trim();
        return s;
    }

    public static string ResolveFolder(BackupScheduleSettings s) =>
        string.IsNullOrWhiteSpace(s.Folder) ? DefaultFolder() : s.Folder;

    private static string Dir(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    /// <summary>Returns an error message when the folder must not be used, otherwise null. Creates the folder and probes it.</summary>
    public static string? ValidateFolder(BackupPaths paths, BackupScheduleSettings s)
    {
        var folder = ResolveFolder(s);
        if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathRooted(folder) || !Path.IsPathFullyQualified(folder))
            return "The backup folder must be a full absolute path, for example D:\\CobaltBackups.";

        // wwwroot is served to anyone who can reach the app; a backup there would be downloadable.
        var full = Dir(folder);
        if (full.StartsWith(Dir(Path.Combine(paths.ContentRoot, "wwwroot")), StringComparison.OrdinalIgnoreCase))
            return "The backup folder cannot be inside the app's wwwroot folder (its contents are served publicly).";
        // A backup folder inside the key ring would put backups next to (and into the reach of) the encryption keys.
        if (full.StartsWith(Dir(Path.Combine(paths.DataDir, "keys")), StringComparison.OrdinalIgnoreCase))
            return "The backup folder cannot be inside the data\\keys folder.";

        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return $"The service cannot write to that folder: {ex.Message}";
        }
        return null;
    }

    public static List<BackupFileInfo> ListBackups(string folder, int max = 30)
    {
        try
        {
            if (!Directory.Exists(folder)) return new();
            return new DirectoryInfo(folder).GetFiles(BackupPrefix + "*.zip")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Take(max)
                .Select(f => new BackupFileInfo { Name = f.Name, SizeBytes = f.Length, CreatedUtc = f.LastWriteTimeUtc })
                .ToList();
        }
        catch { return new(); }
    }

    /// <summary>The most recent scheduled occurrence at or before <paramref name="nowLocal"/>.</summary>
    public static DateTime LastOccurrence(BackupScheduleSettings s, DateTime nowLocal)
    {
        var time = TimeSpan.ParseExact(s.Time, @"hh\:mm", null);
        var candidate = nowLocal.Date + time;
        for (var i = 0; i < 9; i++)
        {
            var dayOk = s.Frequency != "weekly" || (int)candidate.DayOfWeek == s.DayOfWeek;
            if (candidate <= nowLocal && dayOk) return candidate;
            candidate = candidate.AddDays(-1);
        }
        return candidate;
    }

    public static DateTime NextOccurrence(BackupScheduleSettings s, DateTime nowLocal)
    {
        var time = TimeSpan.ParseExact(s.Time, @"hh\:mm", null);
        var candidate = nowLocal.Date + time;
        for (var i = 0; i < 9; i++)
        {
            var dayOk = s.Frequency != "weekly" || (int)candidate.DayOfWeek == s.DayOfWeek;
            if (candidate > nowLocal && dayOk) return candidate;
            candidate = candidate.AddDays(1);
        }
        return candidate;
    }

    /// <summary>
    /// Due-ness comes from the files on disk, not a timer: due when the newest backup is older than the most recent scheduled
    /// occurrence (or there is none). So a restart, downtime or first enable catches up at once.
    /// </summary>
    public static bool IsDue(BackupScheduleSettings s, DateTime nowLocal, BackupFileInfo? newest) =>
        s.Enabled && (newest is null || newest.CreatedUtc.ToLocalTime() < LastOccurrence(s, nowLocal));
}

/// <summary>Takes one backup into the configured folder and prunes old ones. One run at a time.</summary>
public sealed class BackupRunner
{
    private readonly BackupPaths _paths;
    private readonly ILogger<BackupRunner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackupRunner(BackupPaths paths, ILogger<BackupRunner> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public BackupRunResult? Last { get; private set; }
    public bool Running => _gate.CurrentCount == 0;

    public static string Version => typeof(BackupRunner).Assembly.GetName().Version?.ToString() ?? "unknown";

    public async Task<BackupRunResult> RunAsync(string trigger, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var result = new BackupRunResult { StartedAtUtc = DateTime.UtcNow, Trigger = trigger };
        try
        {
            var settings = BackupScheduleStore.Load(_paths.DataDir);
            var folder = BackupScheduleStore.ResolveFolder(settings);
            // The same folder rules as when saving: a hand-edited backup-schedule.json must not escape them.
            if (BackupScheduleStore.ValidateFolder(_paths, settings) is { } problem) throw new InvalidOperationException(problem);

            // Fixed-width, millisecond stamp: names sort in time order, which retention and "newest" rely on.
            var finalPath = Path.Combine(folder, $"{BackupScheduleStore.BackupPrefix}{DateTime.Now:yyyyMMdd-HHmmss-fff}.zip");
            var tmpPath = finalPath + ".tmp";

            try
            {
                await Task.Run(() =>
                {
                    using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        BackupCatalog.WriteZip(fs, _paths.DataDir, Version);
                    File.Move(tmpPath, finalPath, overwrite: false);
                }, ct);
            }
            catch
            {
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
                throw;
            }

            result.File = Path.GetFileName(finalPath);
            result.SizeBytes = new FileInfo(finalPath).Length;
            result.Pruned = Prune(folder, settings.KeepLast);
            result.Success = true;
            _logger.LogInformation("Scheduled backup ({Trigger}) written: {File} ({Size} bytes), {Pruned} old backup(s) removed.",
                trigger, finalPath, result.SizeBytes, result.Pruned);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Error = ex.Message;
            _logger.LogError(ex, "Scheduled backup ({Trigger}) failed.", trigger);
        }
        finally
        {
            result.FinishedAtUtc = DateTime.UtcNow;
            Last = result;
            _gate.Release();
        }
        return result;
    }

    // Only ever deletes files that match our own naming pattern, so pointing the folder at a
    // directory that also holds other files cannot cost the user anything else.
    private int Prune(string folder, int keepLast)
    {
        var removed = 0;
        var stale = new DirectoryInfo(folder).GetFiles(BackupScheduleStore.BackupPrefix + "*.zip")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(Math.Max(1, keepLast));
        foreach (var file in stale)
        {
            try { file.Delete(); removed++; }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not delete old backup {File}.", file.FullName); }
        }
        return removed;
    }
}

/// <summary>
/// Wakes every minute and takes a backup when the schedule says one is due and none has been taken since.
/// Backups are deliberately NOT licence-gated: they keep running while the licence is expired.
/// </summary>
public sealed class BackupScheduleService : BackgroundService
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);

    private readonly BackupRunner _runner;
    private readonly BackupPaths _paths;
    private readonly ILogger<BackupScheduleService> _logger;
    private readonly TimeSpan _firstDelay;
    private DateTime _lastAttemptUtc = DateTime.MinValue;

    public BackupScheduleService(BackupRunner runner, BackupPaths paths, ILogger<BackupScheduleService> logger, TimeSpan? firstDelay = null)
    {
        _runner = runner;
        _paths = paths;
        _logger = logger;
        _firstDelay = firstDelay ?? TimeSpan.FromSeconds(30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(_firstDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = BackupScheduleStore.Load(_paths.DataDir);
                if (settings.Enabled && DateTime.UtcNow - _lastAttemptUtc >= RetryAfterFailure)
                {
                    var newest = BackupScheduleStore.ListBackups(BackupScheduleStore.ResolveFolder(settings), 1).FirstOrDefault();
                    if (BackupScheduleStore.IsDue(settings, DateTime.Now, newest))
                    {
                        _lastAttemptUtc = DateTime.UtcNow;
                        await _runner.RunAsync("scheduled", stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled backup check failed.");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
