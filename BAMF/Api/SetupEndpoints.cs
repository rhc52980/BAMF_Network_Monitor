using System.Text.Json;
using LanWatch.Services;
using static LanWatch.Api.ApiHelpers;

namespace LanWatch.Api;

/// <summary>The first-run setup, and the networks to scan.</summary>
internal static class SetupEndpoints
{
    public static void Map(WebApplication app, AuthService auth)
    {
        // A new install (one whose database BAMF made when it first started) is shown
        // a short setup in the dashboard: which networks to watch, and a password.
        // Everything it sets is in Settings afterwards. An install that was already
        // there, updated to this version, never sees it.
        app.MapGet("/api/setup", (HostStore store, ScannerService scanner) => Results.Json(new
        {
            pending = store.GetSetting("setupPending") == "1",
            networks = NetworksJson(scanner),
            password = auth.Source("admin"),
            minLength = AuthService.MinLength,
        }));

        app.MapPost("/api/setup", (SetupRequest body, HostStore store, ScannerService scanner, HttpContext ctx) =>
        {
            if (store.GetSetting("setupPending") != "1")
                return Results.Conflict(new { error = "BAMF is already set up. Everything setup covers is in Settings." });
            if (body.Skip != true)
            {
                // Everything is checked before anything is saved.
                var labels = new List<string>();
                foreach (var raw in body.Networks ?? [])
                {
                    if (ScannerService.CheckNetwork(raw, out var label) is { } problem) return Results.BadRequest(new { error = problem });
                    if (!labels.Contains(label)) labels.Add(label);
                }
                if (labels.Count == 0) return Results.BadRequest(new { error = "Pick at least one network for BAMF to watch." });
                var setPassword = !auth.Required && body.Open != true;
                if (setPassword && (body.Password ?? "").Length < AuthService.MinLength)
                    return Results.BadRequest(new { error = $"Use at least {AuthService.MinLength} characters for the password, or choose to leave BAMF open." });

                // The networks are saved only if they aren't the ones BAMF would scan
                // anyway, so a list in appsettings.json, or finding them itself, carries on.
                if (!labels.Order().SequenceEqual(scanner.CurrentNetworks().Order()))
                {
                    store.SetSetting("subnets", JsonSerializer.Serialize(labels));
                    scanner.RequestScan(null);
                    app.Logger.LogInformation("Setup: networks {Networks}", string.Join(", ", labels));
                }
                if (setPassword)
                {
                    if (auth.SetPassword("admin", null, body.Password) is { } problem) return Results.BadRequest(new { error = problem });
                    SetSessionCookie(ctx, auth.Issue("admin"));
                }
            }
            store.DeleteSetting("setupPending");
            app.Logger.LogInformation("Setup: {Done}", body.Skip == true ? "skipped" : "done");
            return Results.Ok();
        });

        // Saves the networks to scan, replacing appsettings.json's list, and scans
        // them now. Each must pass CheckNetwork; see /api/settings.
        app.MapPost("/api/settings/networks", (NetworksRequest body, HostStore store, ScannerService scanner) =>
        {
            var labels = new List<string>();
            foreach (var raw in body.Networks ?? [])
            {
                if (ScannerService.CheckNetwork(raw, out var label) is { } problem) return Results.BadRequest(new { error = problem });
                if (!labels.Contains(label)) labels.Add(label);
            }
            if (labels.Count == 0) return Results.BadRequest(new { error = "BAMF needs at least one network to scan." });
            if (labels.Count > 16) return Results.BadRequest(new { error = "That's more networks than BAMF scans at once: 16 at most." });
            store.SetSetting("subnets", JsonSerializer.Serialize(labels));
            scanner.RequestScan(null);
            app.Logger.LogInformation("Networks set in Settings: {Networks}", string.Join(", ", labels));
            return Results.Json(NetworksJson(scanner));
        });

        // Hands the networks back to appsettings.json (or to auto-detect).
        app.MapPost("/api/settings/networks/reset", (HostStore store, ScannerService scanner) =>
        {
            store.DeleteSetting("subnets");
            scanner.RequestScan(null);
            return Results.Json(NetworksJson(scanner));
        });
    }
}
