using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>The clock check: is this machine's clock right? Its switch, the server and the allowance, and asking now.</summary>
internal static class ClockEndpoints
{
    private static DateTime _lastRun = DateTime.MinValue;

    private static object Json(ClockCheck c) => new
    {
        enabled = c.Enabled, server = c.Server, serverSetting = c.ServerSetting, maxSeconds = c.MaxSeconds, last = c.Last,
    };

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/clock", (ClockCheck c) => Results.Json(Json(c)));

        // Switching on asks straight away, so there is something to read.
        app.MapPost("/api/settings/clock-check", async (ActiveArpRequest body, HostStore store, ClockCheck c, CancellationToken ct) =>
        {
            store.SetSetting("clockCheck", body.Enabled ? "true" : "false");
            if (body.Enabled) await c.Check(ct);
            return Results.Json(Json(c));
        });

        app.MapPost("/api/settings/clock", (ClockRequest body, HostStore store, ClockCheck c) =>
        {
            if (body.Server is { } s)
            {
                s = s.Trim();
                if (s != "" && !ClockCheck.ValidServer(s)) return Results.BadRequest(new { error = "A time server's name or address, like pool.ntp.org." });
                store.SetSetting("clockServer", s);
            }
            if (body.MaxSeconds is { } m)
            {
                if (m is < 1 or > 3600) return Results.BadRequest(new { error = "From 1 to 3600 seconds." });
                store.SetSetting("clockMaxSeconds", m.ToString());
            }
            return Results.Json(Json(c));
        });

        // Asks now, whatever the schedule; once every ten seconds at most.
        app.MapPost("/api/clock/run", async (ClockCheck c, CancellationToken ct) =>
        {
            if (DateTime.UtcNow - _lastRun < TimeSpan.FromSeconds(10)) return Results.Json(new { error = "Just asked. Give it a moment." }, statusCode: 429);
            _lastRun = DateTime.UtcNow;
            var r = await c.Check(ct);
            return r is null ? Results.Json(new { error = "It is already asking." }, statusCode: 409) : Results.Json(Json(c));
        });
    }
}

internal sealed record ClockRequest(string? Server, int? MaxSeconds);
