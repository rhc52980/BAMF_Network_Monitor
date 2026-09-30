using LanWatch.Services;
using static LanWatch.Api.ApiHelpers;

namespace LanWatch.Api;

/// <summary>Signing in, and the passwords.</summary>
internal static class SignInEndpoints
{
    public static void Map(WebApplication app, AuthService auth)
    {
        // The sign-in page, and signing in and out from it.
        app.MapGet("/signin", () =>
            Results.File(Path.Combine(app.Environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"), "signin.html"), "text/html; charset=utf-8"));

        app.MapPost("/api/signin", async (SignInRequest body, HttpContext ctx) =>
        {
            if (!auth.Required) return Results.Json(new { role = "open" });
            var address = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
            if (auth.IsLocked(address, out var left))
            {
                await TooManyTries(ctx, left);
                return Results.Empty;
            }
            var role = auth.RoleFor(body.Password ?? "");
            if (role is null)
            {
                auth.Failed(address);
                if (auth.IsLocked(address, out left)) { await TooManyTries(ctx, left); return Results.Empty; }
                return Results.Json(new { error = "That password isn't right." }, statusCode: 401);
            }
            auth.Succeeded(address);
            SetSessionCookie(ctx, auth.Issue(role));
            return Results.Json(new { role });
        });

        app.MapPost("/api/signout", (HttpContext ctx) =>
        {
            SetSessionCookie(ctx, null);
            return Results.Ok();
        });

        // Settings → Security → Sign-in: who's signed in, and where each password
        // comes from; and setting, changing or removing one, which takes the current
        // main password once there is one. Setting or changing the main password
        // signs this browser in with it, so it isn't sent to the sign-in page.
        app.MapGet("/api/auth", (HttpContext ctx) => Results.Json(new
        {
            required = auth.Required,
            role = ctx.Items["bamfRole"] as string ?? "open",
            session = auth.Validate(ctx.Request.Cookies[AuthService.CookieName]) is not null,
            main = auth.Source("admin"),
            viewer = auth.Source("viewer"),
            minLength = AuthService.MinLength,
        }));

        app.MapPost("/api/settings/password", (PasswordRequest body, HttpContext ctx) =>
        {
            var role = body.Role == "viewer" ? "viewer" : "admin";
            if (auth.SetPassword(role, body.Current, body.Password) is { } problem) return Results.BadRequest(new { error = problem });
            if (role == "admin") SetSessionCookie(ctx, auth.Required ? auth.Issue("admin") : null);
            return Results.Ok();
        });
    }
}
