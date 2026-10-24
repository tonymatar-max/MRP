namespace CobaltMrp.Api.Backup;

/// <summary>
/// Backup endpoints, Admin only. They are exempt from the licence gate (see LicenseGate) so a backup can still be
/// taken while the licence is expired. There is deliberately NO restore endpoint: restoring is a manual copy
/// (stop the service, copy the files back into the data folder, start it) so a bad or foreign zip can never
/// overwrite live state through the web.
/// </summary>
public static class BackupEndpoints
{
    public static object View(BackupScheduleSettings settings, BackupRunner runner, BackupPaths paths)
    {
        var folder = BackupScheduleStore.ResolveFolder(settings);
        return new
        {
            settings,
            folder,
            defaultFolder = BackupScheduleStore.DefaultFolder(),
            dataFolder = paths.DataDir,
            contents = BackupCatalog.FileNames.Concat(BackupCatalog.DynamicFilePatterns).ToArray(),
            nextRunLocal = settings.Enabled ? BackupScheduleStore.NextOccurrence(settings, DateTime.Now).ToString("yyyy-MM-dd HH:mm") : null,
            running = runner.Running,
            last = runner.Last,
            backups = BackupScheduleStore.ListBackups(folder),
        };
    }

    public static void MapBackup(this RouteGroupBuilder admin)
    {
        var g = admin.MapGroup("/backup").RequireAuthorization("Admin");

        // Manual download: the SAME writer as the scheduled backup, so the two can never drift.
        g.MapGet("/download", (BackupPaths paths) =>
        {
            var ms = new MemoryStream();
            BackupCatalog.WriteZip(ms, paths.DataDir, BackupRunner.Version);
            ms.Position = 0;
            return Results.File(ms, "application/zip", $"cobalt-mrp-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        });

        g.MapGet("/schedule", (BackupPaths paths, BackupRunner runner) =>
            Results.Ok(View(BackupScheduleStore.Load(paths.DataDir), runner, paths)));

        g.MapPost("/schedule", (BackupScheduleSettings? body, BackupPaths paths, BackupRunner runner) =>
        {
            var settings = BackupScheduleStore.Normalize(body ?? new BackupScheduleSettings());
            if (BackupScheduleStore.ValidateFolder(paths, settings) is { } problem) return Results.BadRequest(new { message = problem });
            BackupScheduleStore.Save(paths.DataDir, settings);
            return Results.Ok(View(settings, runner, paths));
        });

        g.MapPost("/run", async (BackupPaths paths, BackupRunner runner, CancellationToken ct) =>
        {
            var settings = BackupScheduleStore.Load(paths.DataDir);
            if (BackupScheduleStore.ValidateFolder(paths, settings) is { } problem) return Results.BadRequest(new { message = problem });
            var result = await runner.RunAsync("manual", ct);
            return Results.Ok(new { result, view = View(settings, runner, paths) });
        });
    }
}
