using System.Globalization;

namespace LanWatch.Services;

/// <summary>
/// Two looks back over what BAMF has recorded. When a device is usually
/// online, as a week of hours, from its online and offline events. And what
/// changed over a period: devices that arrived or left, addresses that moved,
/// ports that opened or closed, and the alerts raised.
/// </summary>
public partial class HostStore
{
    public sealed record ChangeDevice(long Id, string At, string Detail);
    public sealed record ChangePort(long HostId, int Port, string Service, string At);
    public sealed record Changes(List<ChangeDevice> Arrived, List<ChangeDevice> Left, List<ChangeDevice> Moved,
        List<ChangePort> Opened, List<ChangePort> Closed, Dictionary<string, int> AlertCounts, List<AlertRow> Notable);

    private static DateTime? ParseUtc(string s) =>
        DateTime.TryParse(s, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    /// <summary>
    /// For each day of the week (Monday first) and hour of the day, in the
    /// server's local time, the share of that hour a device was online, over
    /// the last few weeks. Null where BAMF hadn't seen the device yet.
    /// </summary>
    public double?[][] Presence(long hostId, int weeks)
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-7 * weeks);
        DateTime first;
        bool onlineNow;
        var events = new List<(DateTime At, bool Online)>();
        lock (_lock)
        {
            using var conn = Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT first_seen, online FROM hosts WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", hostId);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return Empty();
                first = ParseUtc(r.GetString(0)) ?? now;
                onlineNow = r.GetInt64(1) == 1;
            }
            using (var cmd = conn.CreateCommand())
            {
                // Every event in the window, and the last one before it for the state it opened in.
                cmd.CommandText = """
                    SELECT at, type FROM events WHERE host_id = $id AND at >= $since
                    UNION ALL
                    SELECT at, type FROM (SELECT at, type FROM events WHERE host_id = $id AND at < $since ORDER BY at DESC LIMIT 1)
                    ORDER BY at
                    """;
                cmd.Parameters.AddWithValue("$id", hostId);
                cmd.Parameters.AddWithValue("$since", cutoff.ToString("o"));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (ParseUtc(r.GetString(0)) is { } at) events.Add((at, r.GetString(1) == "online"));
            }
        }

        var start = first > cutoff ? first : cutoff;
        var before = events.Where(e => e.At <= start).ToList();
        var inside = events.Where(e => e.At > start).ToList();
        var state = before.Count > 0 ? before[^1].Online : inside.Count > 0 ? !inside[0].Online : onlineNow;

        var online = new double[7, 24];
        var covered = new double[7, 24];
        var i = 0;
        // Walk the window an hour at a time, splitting each hour where the state changed.
        var hour = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Utc);
        for (; hour < now; hour = hour.AddHours(1))
        {
            var from = hour < start ? start : hour;
            var to = hour.AddHours(1) > now ? now : hour.AddHours(1);
            if (to <= from) continue;
            var local = TimeZoneInfo.ConvertTimeFromUtc(from, TimeZoneInfo.Local);
            var d = ((int)local.DayOfWeek + 6) % 7;   // Monday first
            var h = local.Hour;
            var t = from;
            while (i < inside.Count && inside[i].At < to)
            {
                if (state) online[d, h] += (inside[i].At - t).TotalSeconds;
                t = inside[i].At;
                state = inside[i].Online;
                i++;
            }
            if (state) online[d, h] += (to - t).TotalSeconds;
            covered[d, h] += (to - from).TotalSeconds;
        }
        var grid = Empty();
        for (var d = 0; d < 7; d++)
            for (var h = 0; h < 24; h++)
                grid[d][h] = covered[d, h] < 60 ? null : Math.Round(online[d, h] / covered[d, h], 3);
        return grid;

        static double?[][] Empty() => Enumerable.Range(0, 7).Select(_ => new double?[24]).ToArray();
    }

    /// <summary>What changed since a moment: arrivals, departures, moves, ports and alerts.</summary>
    public Changes ChangesSince(DateTime sinceUtc)
    {
        var since = sinceUtc.ToString("o");
        var all = GetAll().Where(h => !h.Forgotten && !h.Ignored).ToDictionary(h => h.Id);

        var arrived = all.Values.Where(h => string.CompareOrdinal(h.FirstSeen, since) >= 0)
            .OrderByDescending(h => h.FirstSeen).Select(h => new ChangeDevice(h.Id, h.FirstSeen, h.Vendor)).ToList();
        // Left: offline now, and last seen inside the period.
        var left = all.Values.Where(h => !h.Online && string.CompareOrdinal(h.LastSeen, since) >= 0)
            .OrderByDescending(h => h.LastSeen).Select(h => new ChangeDevice(h.Id, h.LastSeen, "")).ToList();

        var moved = new List<ChangeDevice>();
        var opened = new List<ChangePort>();
        var closed = new List<ChangePort>();
        lock (_lock)
        {
            using var conn = Open();
            // An address change: a history row in the period with an earlier row before it.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT h.host_id, h.ip, h.at,
                           (SELECT p.ip FROM ip_history p WHERE p.host_id = h.host_id AND p.id < h.id ORDER BY p.id DESC LIMIT 1)
                    FROM ip_history h WHERE h.at >= $since ORDER BY h.at DESC
                    """;
                cmd.Parameters.AddWithValue("$since", since);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(3) || !all.ContainsKey(r.GetInt64(0))) continue;
                    moved.Add(new ChangeDevice(r.GetInt64(0), r.GetString(2), $"{r.GetString(3)} → {r.GetString(1)}"));
                }
            }
            // A port that opened: first seen in the period, but not on the device's first scan.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT p.host_id, p.port, p.service, p.first_seen, p.open, p.last_seen,
                           (SELECT MIN(q.first_seen) FROM host_ports q WHERE q.host_id = p.host_id)
                    FROM host_ports p
                    WHERE p.first_seen >= $since OR (p.open = 0 AND p.last_seen >= $since)
                    """;
                cmd.Parameters.AddWithValue("$since", since);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var hostId = r.GetInt64(0);
                    if (!all.ContainsKey(hostId)) continue;
                    var firstSeen = r.GetString(3);
                    var isOpen = r.GetInt64(4) == 1;
                    var firstScan = firstSeen == r.GetString(6);
                    if (isOpen && string.CompareOrdinal(firstSeen, since) >= 0 && !firstScan)
                        opened.Add(new ChangePort(hostId, r.GetInt32(1), r.GetString(2), firstSeen));
                    else if (!isOpen)
                        closed.Add(new ChangePort(hostId, r.GetInt32(1), r.GetString(2), r.GetString(5)));
                }
            }
        }
        var alerts = GetAlerts(500, sinceUtc);
        var counts = alerts.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count());
        var notable = alerts.Where(a => a.Kind is "security" or "cert" or "port" or "dhcp" or "dns").Take(10).ToList();
        return new Changes(arrived, left, moved, opened.OrderByDescending(p => p.At).ToList(),
            closed.OrderByDescending(p => p.At).ToList(), counts, notable);
    }
}
