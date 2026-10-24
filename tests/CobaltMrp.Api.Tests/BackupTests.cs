using System.IO.Compression;
using System.Text.Json;
using CobaltMrp.Api.Backup;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CobaltMrp.Api.Tests;

/// <summary>The scheduled backup: what it contains, when it is due, retention and folder safety.</summary>
public class BackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cobalt-bk-test-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");
    private string Out => Path.Combine(_root, "out");
    private readonly BackupPaths _paths;

    public BackupTests()
    {
        Directory.CreateDirectory(Path.Combine(Data, "keys"));
        Directory.CreateDirectory(Path.Combine(_root, "wwwroot"));
        _paths = new BackupPaths(_root, Data);
        foreach (var n in BackupCatalog.FileNames) File.WriteAllText(Path.Combine(Data, n), "{\"n\":\"" + n + "\"}");
        File.WriteAllText(Path.Combine(Data, "keys", "key-1111.xml"), "<key id='1'/>");
        File.WriteAllText(Path.Combine(Data, "keys", "key-2222.xml"), "<key id='2'/>");
        File.WriteAllText(Path.Combine(Data, "keys", "half-written.tmp"), "x");
        File.WriteAllText(Path.Combine(Data, "random-other.json"), "{}");
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private BackupRunner Runner() => new(_paths, NullLogger<BackupRunner>.Instance);
    private void Configure(Action<BackupScheduleSettings>? edit = null)
    {
        var s = new BackupScheduleSettings { Enabled = true, Folder = Out, KeepLast = 3 };
        edit?.Invoke(s);
        BackupScheduleStore.Save(Data, s);
    }
    private static HashSet<string> Members(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Select(e => e.FullName).ToHashSet();
    }

    // ---------------------------------------------------------------- catalog

    [Fact] public void Catalog_ListsEveryStateFile_IncludingTheKeyRing()
    {
        Assert.Equal(new[] { "settings.json", "users.json", "automation-state.json", "backup-schedule.json", "license.json" }.OrderBy(x => x),
                     BackupCatalog.FileNames.OrderBy(x => x));
        Assert.Contains("keys/*", BackupCatalog.DynamicFilePatterns);
    }

    [Fact] public void Zip_ContainsStateFiles_Keys_AndManifest_ButNothingElse()
    {
        using var ms = new MemoryStream();
        BackupCatalog.WriteZip(ms, Data, "1.0");
        ms.Position = 0;
        using var zip = new ZipArchive(ms);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        var expected = new[] { "backup-info.json", "settings.json", "users.json", "automation-state.json", "backup-schedule.json", "license.json", "keys/key-1111.xml", "keys/key-2222.xml" };
        Assert.True(names.SetEquals(expected), "got: " + string.Join(", ", names.OrderBy(x => x)));   // no random-other.json, no *.tmp
        using var info = JsonDocument.Parse(new StreamReader(zip.GetEntry("backup-info.json")!.Open()).ReadToEnd());
        Assert.Contains("keys/key-1111.xml", info.RootElement.GetProperty("Files").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("SQL", info.RootElement.GetProperty("Note").GetString());
        using var k = new StreamReader(zip.GetEntry("keys/key-2222.xml")!.Open());
        Assert.Equal("<key id='2'/>", k.ReadToEnd());
    }

    [Fact] public void Zip_SkipsMissingFiles_WithoutFailing()
    {
        File.Delete(Path.Combine(Data, "license.json"));
        Directory.Delete(Path.Combine(Data, "keys"), true);
        using var ms = new MemoryStream();
        BackupCatalog.WriteZip(ms, Data, "1.0");
        ms.Position = 0;
        using var zip = new ZipArchive(ms);
        Assert.DoesNotContain(zip.Entries, e => e.FullName == "license.json" || e.FullName.StartsWith("keys/"));
        Assert.Contains(zip.Entries, e => e.FullName == "settings.json");
    }

    [Theory]
    [InlineData("settings.json", true)] [InlineData("license.json", true)] [InlineData("keys/key-1.xml", true)]
    [InlineData("random-other.json", false)] [InlineData("../settings.json", false)] [InlineData("keys/../settings.json", false)]
    [InlineData("keys\\key-1.xml", false)] [InlineData("keys/sub/key.xml", false)] [InlineData("C:/x.json", false)] [InlineData("/settings.json", false)]
    [InlineData("keys/", false)] [InlineData("", false)] [InlineData("Settings.JSON", false)]
    public void RestoreAllowList_IsTheCatalog_AndNothingElse(string entry, bool allowed) =>
        Assert.Equal(allowed, BackupCatalog.IsAllowedEntry(entry));

    // ---------------------------------------------------------------- schedule maths

    [Fact] public void Normalize_Clamps()
    {
        var n = BackupScheduleStore.Normalize(new BackupScheduleSettings { Frequency = "WEEKLY", Time = "25:99", DayOfWeek = 9, KeepLast = 0, Folder = "  x  " });
        Assert.Equal("weekly", n.Frequency); Assert.Equal("02:00", n.Time); Assert.Equal(6, n.DayOfWeek); Assert.Equal(1, n.KeepLast); Assert.Equal("x", n.Folder);
        Assert.Equal(365, BackupScheduleStore.Normalize(new BackupScheduleSettings { KeepLast = 9999 }).KeepLast);
    }

    [Fact] public void Schedule_IsItsOwnFile_AndRoundTrips()
    {
        Configure(s => { s.Frequency = "weekly"; s.Time = "07:30"; s.DayOfWeek = 3; });
        Assert.True(File.Exists(Path.Combine(Data, "backup-schedule.json")));
        var l = BackupScheduleStore.Load(Data);
        Assert.True(l.Enabled); Assert.Equal("weekly", l.Frequency); Assert.Equal("07:30", l.Time); Assert.Equal(3, l.DayOfWeek); Assert.Equal(Out, l.Folder);
        File.WriteAllText(Path.Combine(Data, "backup-schedule.json"), "garbage");
        Assert.False(BackupScheduleStore.Load(Data).Enabled);      // corrupt file = safe defaults, no crash
    }

    private static readonly DateTime WedNoon = new(2026, 10, 21, 12, 0, 0);   // a Wednesday

    [Fact] public void Occurrences_Daily_AndWeekly()
    {
        var daily = new BackupScheduleSettings { Frequency = "daily", Time = "02:00" };
        Assert.Equal(new DateTime(2026, 10, 21, 2, 0, 0), BackupScheduleStore.LastOccurrence(daily, WedNoon));
        Assert.Equal(new DateTime(2026, 10, 20, 2, 0, 0), BackupScheduleStore.LastOccurrence(daily, new DateTime(2026, 10, 21, 1, 0, 0)));
        Assert.Equal(new DateTime(2026, 10, 22, 2, 0, 0), BackupScheduleStore.NextOccurrence(daily, WedNoon));
        var mon = new BackupScheduleSettings { Frequency = "weekly", Time = "07:30", DayOfWeek = 1 };
        Assert.Equal(new DateTime(2026, 10, 19, 7, 30, 0), BackupScheduleStore.LastOccurrence(mon, WedNoon));
        Assert.Equal(new DateTime(2026, 10, 26, 7, 30, 0), BackupScheduleStore.NextOccurrence(mon, WedNoon));
    }

    private static BackupFileInfo Backup(DateTime localTime) => new() { Name = "x.zip", CreatedUtc = localTime.ToUniversalTime() };

    [Fact] public void IsDue_CatchesUpAfterDowntime_AndFirstEnable()
    {
        var s = new BackupScheduleSettings { Enabled = true, Frequency = "daily", Time = "02:00" };
        Assert.True(BackupScheduleStore.IsDue(s, WedNoon, null));                                         // none yet: due at once
        Assert.True(BackupScheduleStore.IsDue(s, WedNoon, Backup(new DateTime(2026, 10, 19, 2, 5, 0)))); // service was down two nights
        Assert.False(BackupScheduleStore.IsDue(s, WedNoon, Backup(new DateTime(2026, 10, 21, 2, 1, 0)))); // already taken since 02:00 today
        Assert.True(BackupScheduleStore.IsDue(s, WedNoon, Backup(new DateTime(2026, 10, 20, 23, 0, 0)))); // before today's occurrence
        Assert.False(BackupScheduleStore.IsDue(new BackupScheduleSettings { Enabled = false, Time = "02:00" }, WedNoon, null));                           // disabled: never
    }

    // ---------------------------------------------------------------- runner: retention + folder safety

    [Fact] public async Task Retention_KeepsNewestN_AndNeverTouchesForeignFiles()
    {
        Configure(s => s.KeepLast = 3);
        Directory.CreateDirectory(Out);
        File.WriteAllText(Path.Combine(Out, "keep-me.zip"), "x");
        File.WriteAllText(Path.Combine(Out, "cobalt-wms-backup-auto-20200101-000000.zip"), "x");           // another product's, same folder
        File.WriteAllText(Path.Combine(Out, "cobalt-mrp-backup-20200101-000000.zip"), "x");                // a manual download renamed in
        var runner = Runner();
        BackupRunResult? last = null;
        for (var i = 0; i < 5; i++) { last = await runner.RunAsync("manual", CancellationToken.None); await Task.Delay(15); }
        Assert.True(last!.Success, last.Error);
        Assert.Equal(3, Directory.GetFiles(Out, BackupScheduleStore.BackupPrefix + "*.zip").Length);
        Assert.True(File.Exists(Path.Combine(Out, last.File)));                         // the newest run always survives pruning, even within one second
        Assert.True(File.Exists(Path.Combine(Out, "keep-me.zip")));
        Assert.True(File.Exists(Path.Combine(Out, "cobalt-wms-backup-auto-20200101-000000.zip")));
        Assert.True(File.Exists(Path.Combine(Out, "cobalt-mrp-backup-20200101-000000.zip")));
        Assert.Empty(Directory.GetFiles(Out, "*.tmp"));
        Assert.Contains("keys/key-1111.xml", Members(Directory.GetFiles(Out, BackupScheduleStore.BackupPrefix + "*.zip").OrderBy(x => x).Last()));
    }

    [Fact] public async Task Backup_Succeeds_WhileAStateFileIsOpenForWrite()
    {
        Configure();
        using var open = new FileStream(Path.Combine(Data, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.True((await Runner().RunAsync("manual", CancellationToken.None)).Success);
    }

    [Fact] public void FolderValidation_Rules()
    {
        string? V(string folder) => BackupScheduleStore.ValidateFolder(_paths, new BackupScheduleSettings { Folder = folder });
        Assert.NotNull(V(@"rel\dir"));
        Assert.NotNull(V(@"\\"));
        Assert.NotNull(V(Path.Combine(_root, "wwwroot")));
        Assert.NotNull(V(Path.Combine(_root, "wwwroot", "x", "y")));
        Assert.NotNull(V(Path.Combine(_root, "WWWROOT", "x")));                       // case-insensitive
        Assert.NotNull(V(Path.Combine(_root, "wwwroot", "..", "wwwroot", "z")));      // dot-dot tricks
        Assert.NotNull(V(Path.Combine(Data, "keys")));
        Assert.NotNull(V(Path.Combine(Data, "settings.json", "sub")));               // cannot be created
        Assert.Null(V(Path.Combine(_root, "wwwroot-backup")));                        // a sibling with a similar name is fine
        Assert.Null(V(Out));
        Assert.True(Directory.Exists(Out));                                           // created on validation
    }

    [Fact] public async Task HandEditedSchedule_WithWwwrootFolder_IsRefused_ByTheRunnerToo()
    {
        Configure(s => s.Folder = Path.Combine(_root, "wwwroot", "bk"));
        var r = await Runner().RunAsync("scheduled", CancellationToken.None);
        Assert.False(r.Success); Assert.Contains("wwwroot", r.Error);
        Assert.False(Directory.Exists(Path.Combine(_root, "wwwroot", "bk")));
    }

    [Fact] public async Task BadFolder_IsAFailureResult_NotAnException()
    {
        Configure(s => s.Folder = Path.Combine(Data, "settings.json", "sub"));
        var r = await Runner().RunAsync("scheduled", CancellationToken.None);
        Assert.False(r.Success); Assert.False(string.IsNullOrEmpty(r.Error));
    }

    [Fact] public void DefaultFolder_IsProgramDataCobaltMrp()
    {
        var d = BackupScheduleStore.DefaultFolder();
        Assert.EndsWith(Path.Combine("Cobalt", "MRP", "Backups", "Scheduled"), d);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), d);
    }
}
