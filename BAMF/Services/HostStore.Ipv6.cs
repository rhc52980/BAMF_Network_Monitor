using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// IPv6 addresses seen for each MAC: from this machine's neighbour table and,
/// with the traffic monitor running, from neighbour discovery on the wire.
/// Keyed by MAC, so a device's IPv6 addresses sit beside its IPv4 one, and a
/// MAC that only ever shows up over IPv6 is still on record.
/// </summary>
public partial class HostStore
{
    public sealed record Ipv6Row(string Mac, string Ip, string FirstSeen, string LastSeen);

    private static void InitIpv6(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS ipv6_neighbors (
                mac        TEXT NOT NULL,
                ip         TEXT NOT NULL,
                first_seen TEXT NOT NULL,
                last_seen  TEXT NOT NULL,
                PRIMARY KEY (mac, ip)
            );
            CREATE INDEX IF NOT EXISTS idx_ipv6_seen ON ipv6_neighbors(last_seen);
            """;
        cmd.ExecuteNonQuery();
    }

    public void SeenIpv6(IEnumerable<(string Mac, string Ip, DateTime At)> seen)
    {
        var list = seen.ToList();
        if (list.Count == 0) return;
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var (mac, ip, at) in list)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO ipv6_neighbors (mac, ip, first_seen, last_seen) VALUES ($m, $ip, $at, $at)
                    ON CONFLICT(mac, ip) DO UPDATE SET last_seen = MAX(last_seen, excluded.last_seen)
                    """;
                cmd.Parameters.AddWithValue("$m", mac);
                cmd.Parameters.AddWithValue("$ip", ip);
                cmd.Parameters.AddWithValue("$at", at.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            // Addresses not seen for the history retention window go.
            using (var prune = conn.CreateCommand())
            {
                prune.Transaction = tx;
                prune.CommandText = "DELETE FROM ipv6_neighbors WHERE last_seen < $cutoff";
                prune.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddDays(-_retentionDays).ToString("o"));
                prune.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Every IPv6 address seen since the given time, by MAC.</summary>
    public Dictionary<string, List<Ipv6Row>> GetIpv6(DateTime sinceUtc)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT mac, ip, first_seen, last_seen FROM ipv6_neighbors WHERE last_seen >= $since ORDER BY mac, last_seen DESC";
            cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("o"));
            var map = new Dictionary<string, List<Ipv6Row>>(StringComparer.OrdinalIgnoreCase);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new Ipv6Row(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3));
                if (!map.TryGetValue(row.Mac, out var l)) map[row.Mac] = l = new();
                l.Add(row);
            }
            return map;
        }
    }
}
