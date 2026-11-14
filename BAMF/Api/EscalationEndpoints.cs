using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>Acknowledging an alert, and escalating the ones nobody does: its switch, how long to wait, where it goes and which alerts it watches.</summary>
internal static class EscalationEndpoints
{
    private static object Json(AlertEscalation e) => new
    {
        enabled = e.Enabled, minutes = e.Minutes, to = e.To, toName = e.Destination?.Name, kinds = e.Kinds, groups = AlertEscalation.Groups, waiting = e.Waiting(),
    };

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/escalation", (AlertEscalation e) => Results.Json(Json(e)));

        app.MapPost("/api/settings/escalation", (EscalationRequest body, HostStore store, ScannerService scanner, AlertEscalation e) =>
        {
            if (body.Minutes is { } m)
            {
                if (m < AlertEscalation.MinMinutes || m > AlertEscalation.MaxMinutes) return Results.BadRequest(new { error = $"From {AlertEscalation.MinMinutes} to {AlertEscalation.MaxMinutes} minutes." });
                store.SetSetting("escalateMinutes", m.ToString());
            }
            if (body.To is { } to)
            {
                to = to.Trim();
                if (to != "" && !scanner.Destinations().Any(d => d.Id == to)) return Results.BadRequest(new { error = "That isn't one of your destinations." });
                store.SetSetting("escalateTo", to);
            }
            if (body.Kinds is { } kinds) store.SetSetting("escalateKinds", System.Text.Json.JsonSerializer.Serialize(AlertEscalation.Clean(kinds)));
            if (body.Enabled is { } on)
            {
                if (on)
                {
                    if (e.Destination is null) return Results.BadRequest(new { error = "Pick where escalated alerts go first." });
                    if (e.Kinds.Count == 0) return Results.BadRequest(new { error = "Tick at least one kind of alert to watch." });
                    if (!e.Enabled) store.SetSetting("escalateSince", DateTime.UtcNow.ToString("o"));   // only what is raised from now on
                }
                store.SetSetting("escalate", on ? "true" : "false");
            }
            return Results.Json(Json(e));
        });

        // `key` is one alert's key as /api/alerts gives it; `all` acknowledges every alert that is showing.
        app.MapPost("/api/alerts/ack", (AckRequest body, HostStore store, AlertEscalation e) =>
        {
            var keys = body.All == true ? e.Current().Select(a => a.Key).ToList() : (body.Key ?? "") != "" ? new List<string> { body.Key! } : new List<string>();
            if (keys.Count == 0) return Results.BadRequest(new { error = "Say which alert, or all." });
            return Results.Json(new { acknowledged = store.AcknowledgeAlerts(keys) });
        });
    }
}

internal sealed record EscalationRequest(bool? Enabled, int? Minutes, string? To, List<string>? Kinds);
internal sealed record AckRequest(string? Key, bool? All);
