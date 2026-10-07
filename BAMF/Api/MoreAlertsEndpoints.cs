using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>The public address watch and the new-sign-in alert: their switches.</summary>
internal static class MoreAlertsEndpoints
{
    public static void Map(WebApplication app)
    {
        // Switching the address watch on takes today's address as its starting point and checks straight away.
        app.MapPost("/api/settings/address-watch", async (ActiveArpRequest body, HostStore store, PublicAddressWatch watch, CancellationToken ct) =>
        {
            store.SetSetting("addressWatch", body.Enabled ? "true" : "false");
            string? error = null;
            if (body.Enabled)
            {
                watch.Baseline();
                error = await watch.Check(ct);
            }
            return Results.Json(new { enabled = watch.Enabled, externalIp = store.GetExternalIp(), error });
        });

        app.MapPost("/api/settings/sign-in-alert", (ActiveArpRequest body, HostStore store) =>
        {
            store.SetSetting("signInAlert", body.Enabled ? "true" : "false");
            return Results.Json(new { enabled = body.Enabled });
        });
    }
}
