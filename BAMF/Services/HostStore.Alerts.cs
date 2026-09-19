using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// Three more things BAMF remembers. Alerts it raised (rules, ports, DHCP and
/// DNS), so the Activity tab can show them. Every port it has ever found open
/// on a device, so a port opening later is a change worth an alert. And bytes
/// per device per hour from the traffic monitor, so "this week" means
/// something across restarts. All age out on the history retention window.
/// </summary>
public partial class HostStore
{
    public sealed record AlertRow(long Id, string At, string Kind, string Title, string Detail);
    public sealed record PortRow(int Port, string Service, string FirstSeen, string LastSeen, bool Open);
    public sealed record TrafficHour(string Hour, long Rx, long Tx);

    private static void InitAlerts(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS alerts (
                id     INTEGER PRIMARY KEY AUTOINCREMENT,
                at     TEXT NOT NULL,
                kind   TEXT NOT NULL,
                title  TEXT NOT NULL,
                detail TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS host_ports (
                host_id    INTEGER NOT NULL,
                port       INTEGER NOT NULL,
                service    TEXT NOT NULL DEFAULT '',
                first_seen TEXT NOT NULL,
                last_seen  TEXT NOT NULL,
                open       INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (host_id, port)
            );
            CREATE TABLE IF NOT EXISTS traffic_hourly (
                mac  TEXT NOT NULL,
                hour TEXT NOT NULL,
                rx   INTEGER NOT NULL DEFAULT 0,
                tx   INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (mac, hour)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------ alerts

    public void AddAlert(string kind, string title, string detail)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO alerts (at, kind, title, detail) VALUES ($at, $k, $t, $d)";
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$t", title.Length > 200 ? title[..200] : title);
            cmd.Parameters.AddWithValue("$d", detail.Length > 1000 ? detail[..1000] : detail);
            cmd.ExecuteNonQuery();
        }
    }

    public List<AlertRow> GetAlerts(int limit = 50, DateTime? sinceUtc = null)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, at, kind, title, detail FROM alerts WHERE at >= $since ORDER BY id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$since", (sinceUtc ?? DateTime.MinValue).ToString("o"));
            cmd.Parameters.AddWithValue("$n", limit);
            var list = new List<AlertRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new AlertRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
            return list;
        }
    }

    // ------------------------------------------------------------ ports

    /// <summary>
    /// Records a port scan's result. Every scanned port is marked open or closed;
    /// returns the ports that are open now and weren't known open before (a new
    /// port on a first scan counts too, flagged by <paramref name="firstScan"/>).
    /// </summary>
    public (List<PortRow> NewlyOpen, List<int> Closed, bool FirstScan) RecordPortScan(long hostId, IEnumerable<int> scanned, IEnumerable<(int Port, string Service)> open)
    {
        var now = DateTime.UtcNow.ToString("o");
        var openMap = open.ToDictionary(o => o.Port, o => o.Service);
        var newly = new List<PortRow>();
        var closed = new List<int>();
        lock (_lock)
        {
            using var conn = Open();
            var known = new Dictionary<int, (string Service, string First, bool Open)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT port, service, first_seen, open FROM host_ports WHERE host_id = $h";
                cmd.Parameters.AddWithValue("$h", hostId);
                using var r = cmd.ExecuteReader();
                while (r.Read()) known[r.GetInt32(0)] = (r.GetString(1), r.GetString(2), r.GetInt64(3) == 1);
            }
            var firstScan = known.Count == 0;
            using var tx = conn.BeginTransaction();
            foreach (var port in scanned.Distinct())
            {
                var isOpen = openMap.TryGetValue(port, out var service);
                if (known.TryGetValue(port, out var was))
                {
                    if (isOpen && !was.Open) newly.Add(new PortRow(port, service ?? "", was.First, now, true));
                    if (!isOpen && was.Open) closed.Add(port);
                    using var up = conn.CreateCommand();
                    up.Transaction = tx;
                    up.CommandText = isOpen
                        ? "UPDATE host_ports SET service = $s, last_seen = $now, open = 1 WHERE host_id = $h AND port = $p"
                        : "UPDATE host_ports SET open = 0 WHERE host_id = $h AND port = $p";
                    up.Parameters.AddWithValue("$s", service ?? was.Service);
                    up.Parameters.AddWithValue("$now", now);
                    up.Parameters.AddWithValue("$h", hostId);
                    up.Parameters.AddWithValue("$p", port);
                    up.ExecuteNonQuery();
                }
                else if (isOpen)
                {
                    newly.Add(new PortRow(port, service ?? "", now, now, true));
                    using var ins = conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO host_ports (host_id, port, service, first_seen, last_seen, open) VALUES ($h, $p, $s, $now, $now, 1)";
                    ins.Parameters.AddWithValue("$h", hostId);
                    ins.Parameters.AddWithValue("$p", port);
                    ins.Parameters.AddWithValue("$s", service ?? "");
                    ins.Parameters.AddWithValue("$now", now);
                    ins.ExecuteNonQuery();
                }
            }
            tx.Commit();
            return (newly, closed, firstScan);
        }
    }

    public List<PortRow> GetPorts(long hostId)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT port, service, first_seen, last_seen, open FROM host_ports WHERE host_id = $h ORDER BY open DESC, port";
            cmd.Parameters.AddWithValue("$h", hostId);
            var list = new List<PortRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new PortRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) == 1));
            return list;
        }
    }

    /// <summary>host id -> open ports, for every host with any.</summary>
    public Dictionary<long, List<int>> OpenPorts()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT host_id, port FROM host_ports WHERE open = 1 ORDER BY host_id, port";
            var map = new Dictionary<long, List<int>>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!map.TryGetValue(r.GetInt64(0), out var l)) map[r.GetInt64(0)] = l = new();
                l.Add(r.GetInt32(1));
            }
            return map;
        }
    }

    // ------------------------------------------------------------ traffic history

    /// <summary>Adds bytes to a device's hour. hour is an ISO timestamp truncated to the hour, UTC.</summary>
    public void AddTraffic(IReadOnlyCollection<(string Mac, string Hour, long Rx, long Tx)> rows)
    {
        if (rows.Count == 0) return;
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var (mac, hour, rx, txb) in rows)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO traffic_hourly (mac, hour, rx, tx) VALUES ($m, $h, $rx, $tx)
                    ON CONFLICT(mac, hour) DO UPDATE SET rx = rx + $rx, tx = tx + $tx
                    """;
                cmd.Parameters.AddWithValue("$m", mac);
                cmd.Parameters.AddWithValue("$h", hour);
                cmd.Parameters.AddWithValue("$rx", rx);
                cmd.Parameters.AddWithValue("$tx", txb);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public List<TrafficHour> GetTrafficHistory(string mac, int days)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 90)).ToString("yyyy-MM-ddTHH:00:00Z");
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT hour, rx, tx FROM traffic_hourly WHERE mac = $m AND hour >= $since ORDER BY hour";
            cmd.Parameters.AddWithValue("$m", mac);
            cmd.Parameters.AddWithValue("$since", since);
            var list = new List<TrafficHour>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new TrafficHour(r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
            return list;
        }
    }

    /// <summary>mac -> (rx, tx) totals since a moment.</summary>
    public Dictionary<string, (long Rx, long Tx)> TrafficTotals(DateTime sinceUtc)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT mac, SUM(rx), SUM(tx) FROM traffic_hourly WHERE hour >= $since GROUP BY mac";
            cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("yyyy-MM-ddTHH:00:00Z"));
            var map = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2));
            return map;
        }
    }

    private static void PruneAlertsAndTraffic(SqliteConnection conn, string cutoff)
    {
        foreach (var sql in new[] { "DELETE FROM alerts WHERE at < $cutoff", "DELETE FROM traffic_hourly WHERE hour < $cutoff" })
        {
            using var prune = conn.CreateCommand();
            prune.CommandText = sql;
            prune.Parameters.AddWithValue("$cutoff", cutoff);
            try { prune.ExecuteNonQuery(); } catch (SqliteException) { /* not created yet on first Init */ }
        }
    }
}
