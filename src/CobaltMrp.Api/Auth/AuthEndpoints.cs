using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CobaltMrp.Api.Auth;

public sealed record Credentials(string? Username, string? Password);
public sealed record NewUser(string? Username, string? Password, string? Role);
public sealed record NewPassword(string? Password);

public static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapGet("/status", (HttpContext ctx, UserStore users) => Results.Ok(new
        {
            setupRequired = !users.HasUsers,
            authenticated = ctx.User.Identity?.IsAuthenticated == true,
            user = ctx.User.Identity?.IsAuthenticated == true
                ? new { username = ctx.User.Identity!.Name, role = ctx.User.FindFirstValue(ClaimTypes.Role) }
                : null,
        }));

        // First run only: create the first administrator, and only from a browser on the server itself.
        auth.MapPost("/setup", async (Credentials body, HttpContext ctx, UserStore users) =>
        {
            if (users.HasUsers)
                return Results.BadRequest(new { message = "Setup is already complete." });
            if (!IsLoopback(ctx))
                return Results.BadRequest(new { message = "Create the first administrator from a browser on the server itself (http://localhost)." });

            var err = users.Create(body.Username ?? "", body.Password ?? "", Roles.Admin);
            if (err is not null) return Results.BadRequest(new { message = err });
            await SignIn(ctx, body.Username!.Trim(), Roles.Admin);
            return Results.Ok(new { username = body.Username!.Trim(), role = Roles.Admin });
        });

        auth.MapPost("/login", async (Credentials body, HttpContext ctx, UserStore users) =>
        {
            var u = users.Validate(body.Username ?? "", body.Password ?? "", out var lockedOut);
            if (lockedOut)
                return Results.Json(new { message = "Too many failed attempts. Try again in a few minutes." }, statusCode: 429);
            if (u is null) return Results.Json(new { message = "Invalid username or password." }, statusCode: 401);
            await SignIn(ctx, u.Username, u.Role);
            return Results.Ok(new { username = u.Username, role = u.Role });
        });

        auth.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        var usersApi = app.MapGroup("/api/users").RequireAuthorization("Admin");

        usersApi.MapGet("/", (UserStore users) =>
            users.List().Select(u => new { username = u.Username, role = u.Role }));

        usersApi.MapPost("/", (NewUser body, UserStore users) =>
        {
            var err = users.Create(body.Username ?? "", body.Password ?? "", body.Role ?? "");
            return err is null
                ? Results.Created($"/api/users/{body.Username!.Trim()}", new { username = body.Username!.Trim(), role = body.Role })
                : Results.BadRequest(new { message = err });
        });

        usersApi.MapDelete("/{username}", (string username, HttpContext ctx, UserStore users) =>
        {
            var err = users.Delete(username, ctx.User.Identity?.Name ?? "");
            return err is null ? Results.NoContent() : Results.BadRequest(new { message = err });
        });

        usersApi.MapPost("/{username}/password", (string username, NewPassword body, UserStore users) =>
        {
            var err = users.SetPassword(username, body.Password ?? "");
            return err is null ? Results.NoContent() : Results.BadRequest(new { message = err });
        });
    }

    private static bool IsLoopback(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        return ip is null || System.Net.IPAddress.IsLoopback(ip);
    }

    private static Task SignIn(HttpContext ctx, string username, string role)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role) },
            CookieAuthenticationDefaults.AuthenticationScheme);
        return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }
}
