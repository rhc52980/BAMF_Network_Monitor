using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>"Same phone, new address?": the records that look like one phone with a changing MAC address, and combining or dismissing a suggestion.</summary>
internal static class SameDeviceEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/same-device", (HostStore store) =>
        {
            var on = store.SameDeviceHintsEnabled;
            return Results.Json(new
            {
                enabled = on,
                groups = !on ? [] : store.SameDeviceGroups().Select(g => new
                {
                    g.Key, g.Name, g.KeepId,
                    members = g.Members.Select(m => new { m.Id, m.Mac, m.Ip, m.Subnet, m.Online, m.Ignored, m.FirstSeen, m.LastSeen }).ToList(),
                }).ToList(),
            });
        });

        // Combines a suggested group into one device: the ordinary Network cards combining, so Separate undoes it.
        app.MapPost("/api/same-device/combine", (SameDeviceRequest body, HostStore store) =>
        {
            var (combined, error) = store.CombineSameDevice(body.Key ?? "");
            return error is not null && combined == 0 ? Results.BadRequest(new { error }) : Results.Json(new { combined, error });
        });

        app.MapPost("/api/same-device/dismiss", (SameDeviceRequest body, HostStore store) =>
            store.DismissSameDevice(body.Key ?? "") ? Results.Ok() : Results.BadRequest(new { error = "That suggestion is out of date." }));

        app.MapPost("/api/settings/same-device-hints", (ActiveArpRequest body, HostStore store) =>
        {
            store.SetSetting("sameDeviceHints", body.Enabled ? "true" : "false");
            return Results.Ok();
        });
    }
}

internal sealed record SameDeviceRequest(string? Key);
