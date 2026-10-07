using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>Where devices talk on the internet, and the switches for the flow watch, the bandwidth spike alert and the first-week report.</summary>
internal static class FlowEndpoints
{
    public static void Map(WebApplication app)
    {
        // Per device: how many outside networks it has talked to, how many of those are new this week, and its three biggest.
        app.MapGet("/api/flows", (HostStore store, FlowWatch flows, ScannerService scanner) =>
        {
            var hosts = store.GetAll().Where(h => !h.Forgotten).ToDictionary(h => h.Mac, StringComparer.OrdinalIgnoreCase);
            var now = DateTime.UtcNow;
            return Results.Json(new
            {
                enabled = flows.Enabled,
                spikeAlert = flows.SpikeEnabled,
                firstWeekReport = flows.FirstWeekEnabled,
                listening = scanner.TrafficMonitorEnabled && scanner.Traffic.Running,
                learnDays = FlowWatch.LearnDays,
                maxKnownForAlert = FlowWatch.MaxKnownForAlert,
                devices = flows.Summary(now).Select(d =>
                {
                    hosts.TryGetValue(d.Mac, out var h);
                    var first = DateTime.TryParse(d.FirstSeen, null, System.Globalization.DateTimeStyles.RoundtripKind, out var f) ? f.ToUniversalTime() : now;
                    return new
                    {
                        d.Mac, hostId = h?.Id, name = h is null ? d.Mac : h.CustomName != "" ? h.CustomName : h.Hostname is { Length: > 0 } and not "—" ? h.Hostname : h.Ip,
                        ip = h?.Ip, d.Networks, d.NewThisWeek, d.Bytes, firstSeen = d.FirstSeen,
                        learning = now - first < TimeSpan.FromDays(FlowWatch.LearnDays),
                        watched = now - first >= TimeSpan.FromDays(FlowWatch.LearnDays) && d.Networks <= FlowWatch.MaxKnownForAlert,
                        top = d.Top.Select(t => new { net = t.Net, sample = t.Sample, bytes = t.Bytes, lastSeen = t.LastSeen }),
                    };
                }),
            });
        });

        app.MapPost("/api/settings/flow-watch", (ActiveArpRequest body, HostStore store) =>
        {
            store.SetSetting("flowWatch", body.Enabled ? "true" : "false");
            return Results.Json(new { enabled = body.Enabled });
        });

        app.MapPost("/api/settings/spike-alert", (ActiveArpRequest body, HostStore store) =>
        {
            store.SetSetting("spikeAlert", body.Enabled ? "true" : "false");
            return Results.Json(new { enabled = body.Enabled });
        });

        app.MapPost("/api/settings/first-week-report", (ActiveArpRequest body, HostStore store) =>
        {
            store.SetSetting("firstWeekReport", body.Enabled ? "true" : "false");
            return Results.Json(new { enabled = body.Enabled });
        });
    }
}
