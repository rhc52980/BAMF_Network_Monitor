using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>What the 3D tab draws beyond the devices: where they talk and who is scanning, and the last day to replay.</summary>
internal static class Map3dEndpoints
{
    public static void Map(WebApplication app)
    {
        // Outside networks as seen from the other end, and scans said lately; each with the devices (by id) behind it.
        app.MapGet("/api/flows/map", (HostStore store, FlowWatch flows) =>
        {
            var now = DateTime.UtcNow;
            var hosts = store.GetAll().Where(h => !h.Forgotten && !h.Ignored).ToDictionary(h => h.Mac, StringComparer.OrdinalIgnoreCase);
            var firstFlow = flows.Summary(now).ToDictionary(d => d.Mac, d => d.FirstSeen, StringComparer.OrdinalIgnoreCase);
            var day = now.AddHours(-24).ToString("o");
            bool Learned(string mac) => firstFlow.TryGetValue(mac, out var f)
                && DateTime.TryParse(f, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                && now - at.ToUniversalTime() >= TimeSpan.FromDays(FlowWatch.LearnDays);
            return Results.Json(new
            {
                enabled = flows.Enabled,
                destinations = flows.Destinations(now).Select(d => new
                {
                    net = d.Net, sample = d.Sample, firstSeen = d.FirstSeen, lastSeen = d.LastSeen, bytes = d.Bytes,
                    isNew = string.CompareOrdinal(d.FirstSeen, day) > 0 && d.Macs.Any(Learned),
                    devices = d.Macs.Where(m => hosts.ContainsKey(m)).Select(m => hosts[m].Id).ToList(),
                }).Where(d => d.devices.Count > 0).ToList(),
                scans = flows.RecentScans(now).Where(s => hosts.ContainsKey(s.Mac)).Select(s => new
                {
                    hostId = hosts[s.Mac].Id, what = s.What, count = s.Count, target = s.Target, at = s.At.ToUniversalTime().ToString("o"),
                }).ToList(),
            });
        });

        // The last day, for the replay: who existed and was online at its start, every online and offline since, internet outages and
        // unusual findings. The page works out any moment in it from this.
        app.MapGet("/api/timeline", (int? hours, HostStore store) =>
        {
            var span = Math.Clamp(hours ?? 24, 1, 72);
            var to = DateTime.UtcNow;
            var from = to.AddHours(-span);
            var fromIso = from.ToString("o");
            var hosts = store.GetAll().Where(h => !h.Forgotten && !h.Ignored).ToList();
            var ids = hosts.Select(h => h.Id).ToHashSet();
            var events = store.GetEventsSince(from).Where(e => ids.Contains(e.HostId)).ToList();
            var firstEvent = events.GroupBy(e => e.HostId).ToDictionary(g => g.Key, g => g.First().Type);
            return Results.Json(new
            {
                from = fromIso, to = to.ToString("o"),
                hosts = hosts.Select(h =>
                {
                    // Online at the start: the opposite of the first thing it did since, else however it is now.
                    var online0 = firstEvent.TryGetValue(h.Id, out var t) ? t == "offline" : h.Online;
                    var first = string.CompareOrdinal(h.FirstSeen, fromIso) > 0 ? h.FirstSeen : fromIso;
                    return new { id = h.Id, first, online0 };
                }).ToList(),
                events = events.Select(e => new { h = e.HostId, type = e.Type, at = e.At }).ToList(),
                outages = store.GetWanOutageLog(200).Where(o => string.CompareOrdinal(o.End, fromIso) > 0)
                    .Select(o => new { start = o.Start, end = o.End, local = o.Local }).ToList(),
                unusual = store.RecentUnusual(span).Select(u => new { hostId = u.HostId, at = u.At, resolvedAt = u.ResolvedAt, title = u.Title }).ToList(),
            });
        });
    }
}
