using CobaltMrp.Api.Ai;
using CobaltMrp.Api.Services;
using CobaltMrp.Api.Settings;
using CobaltMrp.Data.ServiceLayer;
using Microsoft.Data.SqlClient;

namespace CobaltMrp.Api.Api;

public sealed record TestResult(bool Ok, string Message);

public static class SettingsEndpoints
{
    public static void MapSettings(this WebApplication app)
    {
        var g = app.MapGroup("/api/settings").RequireAuthorization("Admin");
        g.MapAiSettings();

        g.MapGet("/", (SettingsService settings) => Results.Ok(SettingsService.Masked(settings.Get())));

        g.MapPut("/", (AppSettings body, SettingsService settings) =>
            Results.Ok(SettingsService.Masked(settings.Save(body))));

        // The Test buttons send the CURRENT (possibly unsaved) values of one section; an empty password
        // falls back to the stored one. They always answer 200 with ok true/false, never 500.
        g.MapPost("/test/cobalt-db", async (DbSettings? body, SettingsService settings, CancellationToken ct) =>
        {
            var db = WithStoredPassword(body, settings.Get().CobaltDb);
            var cs = db.BuildConnectionString();
            if (cs is null) return Results.Ok(new TestResult(false, "Enter the server and database name first."));
            return Results.Ok(await Safe(async () =>
            {
                try
                {
                    var tables = await CobaltSchema.CountTablesAsync(cs, ct);
                    var ver = await ServerVersionAsync(cs, ct);
                    return tables == CobaltSchema.Tables.Length
                        ? new TestResult(true, $"Connected to {ver}. All {tables} Cobalt MRP tables are present.")
                        : new TestResult(true, $"Connected to {ver}, but only {tables} of {CobaltSchema.Tables.Length} Cobalt MRP tables exist. " +
                                               "Click 'Create / update tables'.");
                }
                catch (SqlException ex) when (ex.Number == 4060)
                {
                    // Login worked but the database is missing: say so, rather than a bare error.
                    var master = new SqlConnectionStringBuilder(cs) { InitialCatalog = "master" }.ConnectionString;
                    try
                    {
                        var ver = await ServerVersionAsync(master, ct);
                        return new TestResult(false, $"Connected to {ver}, but the database does not exist yet. " +
                                                     "Click 'Create / update tables' to create it.");
                    }
                    catch { throw ex; }
                }
            }));
        });

        g.MapPost("/test/b1-db", async (DbSettings? body, SettingsService settings, CancellationToken ct) =>
        {
            var db = WithStoredPassword(body, settings.Get().B1Db);
            var cs = db.BuildConnectionString(readOnlyIntent: true);
            if (cs is null) return Results.Ok(new TestResult(false, "Enter the server and database name first."));
            return Results.Ok(await Safe(async () =>
            {
                await using var conn = new SqlConnection(cs);
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT (SELECT COUNT(*) FROM OITM WHERE InvntItem='Y'), " +
                                  "(SELECT COUNT(*) FROM ORDR WHERE DocStatus='O')";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                return new TestResult(true, $"Connected to {conn.DataSource}/{conn.Database}: " +
                                            $"{r.GetInt32(0)} inventory items, {r.GetInt32(1)} open sales orders. " +
                                            "(Read-only: Cobalt MRP never writes to this database.)");
            }, hint: "Is this a SAP Business One company database (it must contain OITM and ORDR)?"));
        });

        g.MapPost("/test/service-layer", async (ServiceLayerSettings? body, SettingsService settings, CancellationToken ct) =>
        {
            var stored = settings.Get().ServiceLayer;
            var sl = body is { IsConfigured: true } ? body : stored;
            if (string.IsNullOrEmpty(sl.Password)) sl.Password = stored.Password;
            sl.BaseUrl = (sl.BaseUrl ?? "").Trim(); sl.CompanyDb = (sl.CompanyDb ?? "").Trim(); sl.UserName = (sl.UserName ?? "").Trim();
            if (!sl.IsConfigured)
                return Results.Ok(new TestResult(false, "Enter the base URL, company database and user name first."));
            var bad = ServiceLayerDiagnostics.ValidateBaseUrl(sl.BaseUrl);
            if (bad is not null) return Results.Ok(new TestResult(false, bad));
            return Results.Ok(await Safe(async () =>
            {
                using var client = new ServiceLayerClient(new ServiceLayerOptions
                {
                    BaseUrl = sl.BaseUrl, CompanyDb = sl.CompanyDb, UserName = sl.UserName,
                    Password = sl.Password, AllowInvalidCertificates = sl.AllowInvalidCertificates,
                });
                await client.LoginAsync(ct);
                await client.LogoutAsync(ct);
                return new TestResult(true, $"Signed in to {sl.CompanyDb} as {sl.UserName} and signed out again.");
            }, explain: ex => ServiceLayerDiagnostics.Explain(ex, sl.BaseUrl, sl.AllowInvalidCertificates)));
        });

        g.MapPost("/init-schema", async (SettingsService settings, CancellationToken ct) =>
        {
            var cs = settings.Get().CobaltDb.BuildConnectionString();
            if (cs is null) return Results.Ok(new TestResult(false, "Enter the server and database name first, and save."));
            return Results.Ok(await Safe(async () => new TestResult(true, await CobaltSchema.EnsureAsync(cs, ct))));
        });
    }

    private static DbSettings WithStoredPassword(DbSettings? body, DbSettings stored)
    {
        // No section (or an empty one) means "test what is saved".
        var db = body is { IsConfigured: true } ? body : stored.Clone();
        if (string.IsNullOrEmpty(db.Password)) db.Password = stored.Password;
        db.ConnectionStringOverride = DbSettings.UnmaskOverride(db.ConnectionStringOverride, stored.ConnectionStringOverride);
        return db;
    }

    private static async Task<string> ServerVersionAsync(string cs, CancellationToken ct)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync(ct);
        return $"SQL Server {conn.ServerVersion}";
    }

    private static async Task<TestResult> Safe(Func<Task<TestResult>> action, string? hint = null, Func<Exception, string>? explain = null)
    {
        try { return await action(); }
        catch (Exception ex)
        {
            var msg = explain is not null ? explain(ex)
                : ex is SqlException { Number: 4060 } ? "The database does not exist or the login cannot open it." : ex.Message;
            return new TestResult(false, hint is null ? msg : $"{msg} {hint}");
        }
    }
}
