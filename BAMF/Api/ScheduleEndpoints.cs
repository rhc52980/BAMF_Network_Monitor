using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>Off hours: the hours a device should be off, and the alert for finding it on in them.</summary>
internal static class ScheduleEndpoints
{
    public static void Map(WebApplication app)
    {
        // Every device that has hours, with the words for them.
        app.MapGet("/api/off-hours", (HostStore store) => Results.Json(new
        {
            enabled = store.OffHoursAlertEnabled,
            devices = store.GetOffHours().Select(kv => new { hostId = kv.Key, from = kv.Value.From, to = kv.Value.To, days = kv.Value.Days, text = OffHours.Describe(kv.Value) }).ToList(),
        }));

        // Sets a device's off hours; an empty From clears them.
        app.MapPost("/api/hosts/{id:long}/off-hours", (long id, OffHoursRequest body, HostStore store) =>
        {
            var window = string.IsNullOrEmpty(body.From) ? null : new OffWindow(body.From, body.To ?? "", body.Days ?? OffHours.AllDays);
            var error = store.SetOffHours(id, window);
            return error is null ? Results.Ok() : Results.BadRequest(new { error });
        });

        app.MapPost("/api/settings/off-hours-alert", (ActiveArpRequest body, HostStore store) =>
        {
            store.SetSetting("offHoursAlert", body.Enabled ? "true" : "false");
            return Results.Ok();
        });
    }
}

internal sealed record OffHoursRequest(string? From, string? To, int? Days);
