using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>The dynamic DNS hook: its switch, its update URL (kept, never shown again) and updating now.</summary>
internal static class DdnsEndpoints
{
    private static DateTime _lastRun = DateTime.MinValue;

    private static object Json(DdnsUpdater d, HostStore store) => new
    {
        enabled = d.Enabled, hasUrl = d.HasUrl, url = d.HasUrl ? DdnsUpdater.Mask(d.Url) : "", updated = d.PushedIp, last = d.Last,
        externalIp = store.GetExternalIp()?.Ip,
    };

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/ddns", (DdnsUpdater d, HostStore store) => Results.Json(Json(d, store)));

        // `url` empty forgets it (and switches the hook off). Switching on updates at once, so the first answer is there to read.
        app.MapPost("/api/settings/ddns", async (DdnsRequest body, HostStore store, DdnsUpdater d, CancellationToken ct) =>
        {
            if (body.Url is { } u)
            {
                u = u.Trim();
                if (u != "" && DdnsUpdater.Validate(u) is { } bad) return Results.BadRequest(new { error = bad });
                store.SetSetting("ddnsUrl", u);
                if (u == "") store.SetSetting("ddnsEnabled", "false");
                store.SetSetting("ddnsPushed", "");           // a different URL means a different name to update
                store.SetSetting("ddnsFailures", "0");
            }
            if (body.Enabled is { } on)
            {
                if (on && !d.HasUrl) return Results.BadRequest(new { error = "Give it an update URL first." });
                store.SetSetting("ddnsEnabled", on ? "true" : "false");
                if (on) await d.Run(true, ct);
            }
            return Results.Json(Json(d, store));
        });

        // Updates now, whatever the address and the schedule; once every ten seconds at most.
        app.MapPost("/api/ddns/run", async (DdnsUpdater d, HostStore store, CancellationToken ct) =>
        {
            if (!d.HasUrl) return Results.Json(new { error = "Give it an update URL first." }, statusCode: 409);
            if (DateTime.UtcNow - _lastRun < TimeSpan.FromSeconds(10)) return Results.Json(new { error = "Just updated. Give it a moment." }, statusCode: 429);
            _lastRun = DateTime.UtcNow;
            var r = await d.Run(true, ct);
            return r is null ? Results.Json(new { error = "It is already updating." }, statusCode: 409) : Results.Json(Json(d, store));
        });
    }
}

internal sealed record DdnsRequest(bool? Enabled, string? Url);
