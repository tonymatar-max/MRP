namespace CobaltMrp.Api.Licensing;

public sealed record SaveLicenseRequest(string? LicenseKey);

public static class LicenseEndpoints
{
    /// <summary>
    /// GET is open: the hardware key must be readable while unlicensed (even before sign-in) so the key can be requested;
    /// it never returns the licence key itself. Saving needs the Admin role, always: sign-in keeps working while
    /// unlicensed, so an administrator can always reach this. Only a signature-checked key is stored.
    /// </summary>
    public static void MapLicense(this RouteGroupBuilder admin)
    {
        admin.MapGet("/license", (ILicenseService license) => Results.Ok(license.Load())).AllowAnonymous();

        admin.MapPost("/license", (SaveLicenseRequest? body, ILicenseService license) =>
        {
            var result = license.Save(body?.LicenseKey ?? "");
            return result.Saved
                ? Results.Ok(result.Status)
                : Results.BadRequest(new { message = result.Status.Message, status = result.Status });
        }).RequireAuthorization("Admin");
    }
}
