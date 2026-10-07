using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// Where each device talks on the internet, as the flow watch saw it: per device, each outside network (a /24) it has sent to or
/// heard from, when first and last, and how much. Kept so a device's habits survive a restart and so the "new destination" rule
/// can tell a week-old habit from something that started today.
/// </summary>
public partial class HostStore
{
    public sealed record FlowRow(string Mac, string Net, string FirstSeen, string LastSeen, long Bytes, long Hits, string SampleIp);
    public sealed record FlowDelta(string Mac, string Net, DateTime At, long Bytes, long Hits, string SampleIp);

    private static void InitFlows(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS flows (
                mac        TEXT NOT NULL,
                net        TEXT NOT NULL,
                first_seen TEXT NOT NULL,
                last_seen  TEXT NOT NULL,
                bytes      INTEGER NOT NULL DEFAULT 0,
                hits       INTEGER NOT NULL DEFAULT 0,
                sample_ip  TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (mac, net)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Adds what was seen since the last flush: a new (device, network) pair is created, a known one is added to.</summary>
    public void AddFlows(IReadOnlyCollection<FlowDelta> deltas)
    {
        if (deltas.Count == 0) return;
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var d in deltas)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO flows (mac, net, first_seen, last_seen, bytes, hits, sample_ip) VALUES ($m, $n, $at, $at, $b, $h, $ip)
                    ON CONFLICT(mac, net) DO UPDATE SET last_seen = $at, bytes = bytes + $b, hits = hits + $h
                    """;
                cmd.Parameters.AddWithValue("$m", d.Mac.ToUpperInvariant());
                cmd.Parameters.AddWithValue("$n", d.Net);
                cmd.Parameters.AddWithValue("$at", d.At.ToString("o"));
                cmd.Parameters.AddWithValue("$b", d.Bytes);
                cmd.Parameters.AddWithValue("$h", d.Hits);
                cmd.Parameters.AddWithValue("$ip", d.SampleIp);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Every flow, or one device's.</summary>
    public List<FlowRow> GetFlows(string? mac = null)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT mac, net, first_seen, last_seen, bytes, hits, sample_ip FROM flows" + (mac is null ? "" : " WHERE mac = $m") + " ORDER BY mac, bytes DESC";
            if (mac is not null) cmd.Parameters.AddWithValue("$m", mac.ToUpperInvariant());
            var list = new List<FlowRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new FlowRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetString(6)));
            return list;
        }
    }

    /// <summary>Flows not seen for longer than the history retention are dropped with the rest.</summary>
    public void PruneFlows(int retentionDays)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM flows WHERE last_seen < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddDays(-Math.Max(7, retentionDays)).ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Bytes in and out per device per UTC day, from the hourly history, since a moment.</summary>
    public Dictionary<string, Dictionary<string, long>> DailyTraffic(DateTime sinceUtc)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT mac, substr(hour, 1, 10), SUM(rx + tx) FROM traffic_hourly WHERE hour >= $since GROUP BY mac, substr(hour, 1, 10)";
            cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("yyyy-MM-ddTHH:00:00Z"));
            var map = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var mac = r.GetString(0);
                if (!map.TryGetValue(mac, out var days)) map[mac] = days = new();
                days[r.GetString(1)] = r.GetInt64(2);
            }
            return map;
        }
    }
}
