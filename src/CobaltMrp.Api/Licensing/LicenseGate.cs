namespace CobaltMrp.Api.Licensing;

/// <summary>
/// The 402 gate. Every /api call is refused until the licence is valid, EXCEPT the sign-in endpoints, the licence
/// endpoints and the backup endpoints (an expired customer must still be able to sign in, read the hardware key,
/// paste a new key and take a backup). Static files are never gated, so the SPA loads and shows the Licence screen.
/// </summary>
public static class LicenseGate
{
    private static readonly PathString[] Exempt =
    [
        new("/api/auth"),
        new("/api/admin/license"),
        new("/api/admin/backup"),
    ];

    public static bool IsExempt(PathString path) =>
        !path.StartsWithSegments("/api") || Exempt.Any(e => path.StartsWithSegments(e));

    public static object Body(LicenseStatus status) => new
    {
        licenseRequired = true,
        reason = status.State.ToString(),
        error = "License expired or missing. Update the license to continue.",
        message = "A valid licence is required. " + status.Message,
    };

    public static IApplicationBuilder UseLicenseGate(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (!IsExempt(ctx.Request.Path))
        {
            var license = ctx.RequestServices.GetRequiredService<ILicenseService>();
            if (!license.IsValid())
            {
                ctx.Response.StatusCode = StatusCodes.Status402PaymentRequired;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsJsonAsync(Body(license.Load()));
                return;
            }
        }
        await next();
    });
}
