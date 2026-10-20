using LanWatch.Services;

namespace LanWatch.Api;

internal sealed record DhcpProbeRequest(bool? Enabled, int? Hours, bool? AlertOnSilence);

/// <summary>The DHCP probe: what it last found, its schedule, and a button to ask now.</summary>
internal static class DhcpProbeEndpoints
{
    private static DateTime _lastRun = DateTime.MinValue;

    private static object Json(DhcpProbe p) => new
    {
        enabled = p.Enabled, hours = p.Hours, alertOnSilence = p.AlertOnSilence, last = p.Last, nextDue = p.NextDue()?.ToString("o"),
        clientMac = string.Join(":", DhcpProbe.ClientMac.Select(b => b.ToString("x2"))),
    };

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/dhcp-probe", (DhcpProbe probe) => Results.Json(Json(probe)));

        app.MapPost("/api/settings/dhcp-probe", (DhcpProbeRequest body, HostStore store, DhcpProbe probe) =>
        {
            if (body.Hours is { } h && h is < 1 or > 168) return Results.BadRequest(new { error = "From 1 to 168 hours." });
            if (body.Enabled is { } on) store.SetSetting("dhcpProbe", on ? "true" : "false");
            if (body.Hours is { } hours) store.SetSetting("dhcpProbeHours", hours.ToString());
            if (body.AlertOnSilence is { } tell) store.SetSetting("dhcpSilentAlert", tell ? "true" : "false");
            return Results.Json(Json(probe));
        });

        // Asks now, whatever the schedule; once every ten seconds at most.
        app.MapPost("/api/dhcp-probe/run", async (DhcpProbe probe, CancellationToken ct) =>
        {
            if (DateTime.UtcNow - _lastRun < TimeSpan.FromSeconds(10)) return Results.Json(new { error = "Just asked. Give it a moment." }, statusCode: 429);
            _lastRun = DateTime.UtcNow;
            var r = await probe.RunOnce(ct);
            return r is null ? Results.Json(new { error = "It is already asking." }, statusCode: 409) : Results.Json(Json(probe));
        });
    }
}
