using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>The network score and the daily all-quiet note: what they say now, and their switches.</summary>
internal static class ScoreEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/score", (HealthScore score) =>
        {
            var s = score.Now();
            return Results.Json(new { enabled = score.Enabled, s.Value, s.Word, reasons = s.Reasons.Select(r => new { r.Text, r.Points }), history = score.History() });
        });

        app.MapPost("/api/settings/score", (ActiveArpRequest body, HostStore store, HealthScore score) =>
        {
            store.SetSetting("networkScore", body.Enabled ? "true" : "false");
            return Results.Json(new { enabled = score.Enabled });
        });

        app.MapGet("/api/all-quiet", (DailyNote note) => Results.Json(AllQuietJson(note)));

        app.MapPost("/api/settings/all-quiet", (AllQuietRequest body, HostStore store, DailyNote note) =>
        {
            if (body.Hour is { } h && h is < 0 or > 23) return Results.BadRequest(new { error = "An hour from 0 to 23." });
            if (body.Enabled is { } on) store.SetSetting("allQuiet", on ? "true" : "false");
            if (body.Hour is { } hour) store.SetSetting("allQuietHour", hour.ToString());
            return Results.Json(AllQuietJson(note));
        });

        app.MapGet("/api/all-quiet/preview", (DailyNote note) =>
        {
            var (title, text, _, quiet) = note.Compose();
            return Results.Json(new { title, text, quiet });
        });

        app.MapPost("/api/all-quiet/send", async (DailyNote note, ScannerService scanner, CancellationToken ct) =>
        {
            if (!scanner.Takes("reports")) return Results.BadRequest(new { error = scanner.AnyDestination
                ? "No destination is set to get reports. Tick Reports on one above."
                : "No webhook is saved, so there's nowhere to send it." });
            var ok = await note.SendAsync(ct);
            return ok ? Results.Json(AllQuietJson(note)) : Results.Json(new { error = "The destination didn't take it. See the Problems card." }, statusCode: 502);
        });
    }

    private static object AllQuietJson(DailyNote note) => new
    {
        enabled = note.Enabled, hour = note.Hour,
        lastSent = note.LastSent?.ToString("o"), next = note.NextDue()?.ToString("o"),
        timeZone = TimeZoneInfo.Local.Id,
    };
}
