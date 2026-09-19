using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// Latency and uptime. After each scan, every online device on the networks
/// that pass covered gets one ICMP echo, and the round-trip time (or the lack
/// of a reply) is kept here, aged out on the same window as events. Uptime is
/// worked out from the online/offline events already recorded: the share of a
/// window a device was online, counted from when it was first seen.
/// </summary>
public partial class HostStore
{
    private static void InitLatency(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS latency (
                host_id INTEGER NOT NULL,
                at      TEXT NOT NULL,
                ms      INTEGER            -- NULL: no reply to the echo
            );
            CREATE INDEX IF NOT EXISTS idx_latency_host ON latency(host_id, at);
            """;
        cmd.ExecuteNonQuery();
    }

    public void RecordLatency(IReadOnlyCollection<(long HostId, int? Ms)> samples)
    {
        if (samples.Count == 0) return;
        var now = DateTime.UtcNow.ToString("o");
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var (id, ms) in samples)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO latency (host_id, at, ms) VALUES ($h, $at, $ms)";
                cmd.Parameters.AddWithValue("$h", id);
                cmd.Parameters.AddWithValue("$at", now);
                cmd.Parameters.AddWithValue("$ms", ms.HasValue ? ms.Value : DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>One device's samples over the last <paramref name="hours"/>, oldest first.</summary>
    public List<(string At, int? Ms)> GetLatency(long hostId, int hours)
    {
        var since = DateTime.UtcNow.AddHours(-Math.Clamp(hours, 1, 24 * 90)).ToString("o");
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT at, ms FROM latency WHERE host_id = $h AND at >= $since ORDER BY at";
            cmd.Parameters.AddWithValue("$h", hostId);
            cmd.Parameters.AddWithValue("$since", since);
            var list = new List<(string, int?)>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetInt32(1)));
            return list;
        }
    }

    /// <summary>Each device's most recent sample: host id -> ms, or null for no reply.</summary>
    public Dictionary<long, int?> LatestLatency()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT l.host_id, l.ms FROM latency l
                JOIN (SELECT host_id, MAX(rowid) AS r FROM latency GROUP BY host_id) m ON m.r = l.rowid
                """;
            var map = new Dictionary<long, int?>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetInt64(0)] = r.IsDBNull(1) ? null : r.GetInt32(1);
            return map;
        }
    }

    private static void PruneLatency(SqliteConnection conn, string cutoff)
    {
        using var prune = conn.CreateCommand();
        prune.CommandText = "DELETE FROM latency WHERE at < $cutoff";
        prune.Parameters.AddWithValue("$cutoff", cutoff);
        try { prune.ExecuteNonQuery(); } catch (SqliteException) { /* not created yet on first Init */ }
    }

    /// <summary>
    /// Share of the last 7 and 30 days each device was online, as percentages,
    /// from its online/offline events. A device counts from when it was first
    /// seen, so a week-old device's 30-day figure is over its own week.
    /// </summary>
    public Dictionary<long, (double? Week, double? Month)> Uptimes()
    {
        var now = DateTime.UtcNow;
        var hosts = new List<(long Id, DateTime First, bool Online)>();
        var events = new Dictionary<long, List<(DateTime At, bool Online)>>();
        lock (_lock)
        {
            using var conn = Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, first_seen, online FROM hosts WHERE forgotten = 0";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (DateTime.TryParse(r.GetString(1), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var first))
                        hosts.Add((r.GetInt64(0), first, r.GetInt64(2) == 1));
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT host_id, at, type FROM events WHERE at >= $since ORDER BY host_id, at";
                cmd.Parameters.AddWithValue("$since", now.AddDays(-31).ToString("o"));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!DateTime.TryParse(r.GetString(1), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)) continue;
                    var id = r.GetInt64(0);
                    if (!events.TryGetValue(id, out var list)) events[id] = list = new();
                    list.Add((at, r.GetString(2) == "online"));
                }
            }
        }

        double? Share(long id, DateTime first, bool onlineNow, int days)
        {
            var start = now.AddDays(-days);
            if (first > start) start = first;
            var span = (now - start).TotalSeconds;
            if (span < 60) return null;
            var evs = events.TryGetValue(id, out var l) ? l : new List<(DateTime At, bool Online)>();
            // State at the start of the window: the last event before it, else
            // the opposite of the first event inside it, else the state now.
            var before = evs.Where(e => e.At <= start).ToList();
            var after = evs.Where(e => e.At > start).ToList();
            bool state = before.Count > 0 ? before[^1].Online : after.Count > 0 ? !after[0].Online : onlineNow;
            double online = 0;
            var t = start;
            foreach (var e in evs)
            {
                if (e.At <= start) continue;
                if (state) online += (e.At - t).TotalSeconds;
                t = e.At; state = e.Online;
            }
            if (state) online += (now - t).TotalSeconds;
            return Math.Round(100 * Math.Clamp(online / span, 0, 1), 1);
        }

        var result = new Dictionary<long, (double?, double?)>();
        foreach (var (id, first, online) in hosts)
            result[id] = (Share(id, first, online, 7), Share(id, first, online, 30));
        return result;
    }
}
