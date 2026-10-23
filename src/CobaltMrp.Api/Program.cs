using CobaltMrp.Api.Ai;
using CobaltMrp.Api.Api;
using CobaltMrp.Api.Auth;
using CobaltMrp.Api.Services;
using CobaltMrp.Api.Settings;
using CobaltMrp.Data;
using CobaltMrp.Engine;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Run under the Windows Service Control Manager when launched as a service (no-op from a console), so
// `sc start` works and the service reports Running/Stopped correctly. UseWindowsService also points the
// content root at the exe folder instead of C:\Windows\System32, which keeps `data` next to the app.
builder.Host.UseWindowsService(o => o.ServiceName = "CobaltMRP");
#pragma warning disable CA1416 // guarded by IsWindows(); the analyser cannot see through the lambda
if (OperatingSystem.IsWindows()) builder.Logging.AddEventLog(o => o.SourceName = "Cobalt MRP");
#pragma warning restore CA1416

var dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);

// Enums travel as names ("PurchaseOrder"), not ordinals, in both directions.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Pin the DataProtection application name so protected secrets survive moving or renaming the app folder.
// Changing this value invalidates every stored secret. Keys are additionally DPAPI-protected (machine scope).
var dp = builder.Services.AddDataProtection()
    .SetApplicationName("CobaltMRP")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));
if (OperatingSystem.IsWindows()) dp.ProtectKeysWithDpapi(protectToLocalMachine: true);

builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton(sp => new SettingsService(
    dataDir, sp.GetRequiredService<SecretProtector>(), sp.GetRequiredService<ILogger<SettingsService>>()));
builder.Services.AddSingleton(new UserStore(dataDir));

// Storage follows the live settings: SQL once the Cobalt database is configured, memory until then.
// The DbContext is only ever resolved when SQL is configured.
builder.Services.AddDbContext<CobaltMrpDbContext>((sp, o) =>
    o.UseSqlServer(sp.GetRequiredService<SettingsService>().Get().CobaltDb.BuildConnectionString()
                   ?? "Server=.;Database=CobaltMrpUnconfigured;Trusted_Connection=True;TrustServerCertificate=True"));
builder.Services.AddSingleton<MemoryRunStore>();
builder.Services.AddSingleton<MemoryMasterStore>();
builder.Services.AddSingleton<MockB1DataProvider>();
builder.Services.AddSingleton<MemoryForecastStore>();
builder.Services.AddScoped<IRunStore>(sp =>
    sp.GetRequiredService<SettingsService>().Get().CobaltDb.IsConfigured
        ? new SqlRunStore(sp.GetRequiredService<CobaltMrpDbContext>())
        : sp.GetRequiredService<MemoryRunStore>());
builder.Services.AddScoped<IForecastStore>(sp =>
    sp.GetRequiredService<SettingsService>().Get().CobaltDb.IsConfigured
        ? new SqlForecastStore(sp.GetRequiredService<CobaltMrpDbContext>())
        : sp.GetRequiredService<MemoryForecastStore>());
builder.Services.AddScoped<IMasterStore>(sp =>
    sp.GetRequiredService<SettingsService>().Get().CobaltDb.IsConfigured
        ? new SqlMasterStore(sp.GetRequiredService<CobaltMrpDbContext>())
        : sp.GetRequiredService<MemoryMasterStore>());
builder.Services.AddSingleton<MemorySapPushLog>();
builder.Services.AddScoped<ISapPushLog>(sp =>
    sp.GetRequiredService<SettingsService>().Get().CobaltDb.IsConfigured
        ? new SqlSapPushLog(sp.GetRequiredService<CobaltMrpDbContext>())
        : sp.GetRequiredService<MemorySapPushLog>());
builder.Services.AddSingleton<MemoryRunTemplateStore>();
builder.Services.AddScoped<IRunTemplateStore>(sp =>
    sp.GetRequiredService<SettingsService>().Get().CobaltDb.IsConfigured
        ? new SqlRunTemplateStore(sp.GetRequiredService<CobaltMrpDbContext>())
        : sp.GetRequiredService<MemoryRunTemplateStore>());
builder.Services.AddScoped<RunPlanner>();
builder.Services.AddScoped<RunLauncher>();
builder.Services.AddScoped<ExceptionNotifier>();
builder.Services.AddSingleton(new AutomationState(dataDir));
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddSingleton<ScheduleGate>();
builder.Services.AddSingleton<IScheduledJob, ScheduledJob>();
builder.Services.AddSingleton(sp => new ScheduleTicker(sp.GetRequiredService<SettingsService>(), sp.GetRequiredService<AutomationState>(),
    sp.GetRequiredService<IScheduledJob>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger<ScheduleTicker>()));
builder.Services.AddHostedService<ScheduledRunService>();
builder.Services.AddSingleton<ReleaseService>();
builder.Services.AddSingleton<ExceptionExecutor>();
builder.Services.AddSingleton<ItemPushService>();
builder.Services.AddSingleton<MrpEngine>();
builder.Services.AddSingleton<ProviderFactory>();

// AI assistant (OpenRouter). The base URL and timeout can be overridden with the Ai:BaseUrl / Ai:TimeoutSeconds
// configuration keys (used by the automated tests to point at a stand-in server); the key and model live in Settings.
builder.Services.AddHttpClient("openrouter", c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var t = cfg.GetValue<int?>("Ai:TimeoutSeconds");
    return new OpenRouterClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("openrouter"),
        cfg["Ai:BaseUrl"], t is > 0 ? TimeSpan.FromSeconds(t.Value) : null);
});
builder.Services.AddSingleton(sp => new AiRateLimiter(sp.GetRequiredService<IConfiguration>().GetValue("Ai:RateLimitPerMinute", 20)));
builder.Services.AddScoped<AiAssistant>();

// Cookie sign-in for the SPA: HttpOnly + SameSite=Strict (the UI is served from the same origin), and plain
// 401/403 responses instead of redirects to a login page.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "cobalt_mrp_auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole(Roles.Admin))
    .AddPolicy("Release", p => p.RequireRole(Roles.Admin, Roles.Buyer));

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
}

var app = builder.Build();

// Bring the Cobalt database forward at startup when it is configured. The service must still start when
// SQL Server is down or the settings are wrong, so a failure is logged and shown on the Settings screen.
{
    var cs = app.Services.GetRequiredService<SettingsService>().Get().CobaltDb.BuildConnectionString();
    if (cs is not null)
    {
        try { app.Logger.LogInformation("Cobalt MRP database: {Result}", await CobaltSchema.EnsureAsync(cs)); }
        catch (Exception ex) { app.Logger.LogError(ex, "Could not prepare the Cobalt MRP database. Check Settings > Cobalt database."); }
    }
}

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    try { await next(); }
    catch (Exception ex) when (ex is not OperationCanceledException && !ctx.Response.HasStarted && ctx.Request.Path.StartsWithSegments("/api"))
    {
        // Safety net: an API call never ends as an empty HTTP 500; the UI always gets {message}.
        app.Logger.LogError(ex, "Unhandled error in {Path}", ctx.Request.Path);
        ctx.Response.Clear();
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { message = "The server hit an unexpected error: " + ex.Message }));
    }
});

if (app.Environment.IsDevelopment())
{
    app.UseCors();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapAuth();
app.MapSettings();
var api = app.MapGroup("/api").RequireAuthorization();
api.MapRuns();
api.MapAi();

// Single-page app: any non-API path that is not a file serves index.html (client-side routing).
app.MapFallback(async ctx =>
{
    var index = Path.Combine(app.Environment.WebRootPath ?? "", "index.html");
    if (ctx.Request.Path.StartsWithSegments("/api") || !File.Exists(index)) { ctx.Response.StatusCode = 404; return; }
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.SendFileAsync(index);
});

app.Run();
