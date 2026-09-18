using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>A device's address and whether it still answers there.</summary>
public record HostAddress(string Ip, string Subnet, string FirstSeen, string LastSeen, bool Current);

/// <summary>
/// Every address a device answers on. A router with an address on each of
/// your networks, or a server with a second IP, is one MAC with several
/// addresses at once. Before this, hosts held one address, so each sighting
/// overwrote the last and the device flip-flopped between them, filling its
/// address history with "moves" that never happened.
///
/// hosts.ip stays the device's main address while it keeps answering; the
/// others are "also at". An address stops being current after as many missed
/// scans as it takes a device to go offline, and only then does the main
/// address move to another current one and count as a change of address.
/// </summary>
public partial class HostStore
{
    private static void InitAddresses(SqliteConnection conn)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS host_addresses (
                    host_id    INTEGER NOT NULL,
                    ip         TEXT NOT NULL,
                    subnet     TEXT NOT NULL DEFAULT '',
                    first_seen TEXT NOT NULL,
                    last_seen  TEXT NOT NULL,
                    misses     INTEGER NOT NULL DEFAULT 0,
                    current    INTEGER NOT NULL DEFAULT 1,
                    PRIMARY KEY (host_id, ip)
                );
                """;
            cmd.ExecuteNonQuery();
        }
        // Existing devices start with the one address they already have, so an
        // upgrade doesn't treat every device's next sighting as a move.
        using (var seed = conn.CreateCommand())
        {
            seed.CommandText = """
                INSERT OR IGNORE INTO host_addresses (host_id, ip, subnet, first_seen, last_seen, current)
                SELECT id, ip, subnet, last_seen, last_seen, online FROM hosts WHERE ip <> ''
                """;
            seed.ExecuteNonQuery();
        }
    }

    /// <summary>Records that a device answered on an address just now.</summary>
    private static void TouchAddress(SqliteConnection conn, long hostId, string ip, string subnet, string now)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO host_addresses (host_id, ip, subnet, first_seen, last_seen, misses, current)
            VALUES ($id, $ip, $subnet, $now, $now, 0, 1)
            ON CONFLICT (host_id, ip) DO UPDATE SET
                subnet = excluded.subnet, last_seen = excluded.last_seen, misses = 0, current = 1
            """;
        cmd.Parameters.AddWithValue("$id", hostId);
        cmd.Parameters.AddWithValue("$ip", ip);
        cmd.Parameters.AddWithValue("$subnet", subnet);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Whether a device still answers on an address.</summary>
    private static bool IsCurrentAddress(SqliteConnection conn, long hostId, string ip)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT current FROM host_addresses WHERE host_id = $id AND ip = $ip";
        cmd.Parameters.AddWithValue("$id", hostId);
        cmd.Parameters.AddWithValue("$ip", ip);
        return cmd.ExecuteScalar() is long c && c == 1;
    }

    /// <summary>
    /// After a scan pass: counts a miss for every address on a scanned network
    /// that didn't answer since <paramref name="passStartUtc"/>, retires those
    /// that reach <paramref name="missThreshold"/>, and moves a device's main
    /// address when it was retired but another still answers. Scoping follows
    /// MarkOffline: null covered means everything was scanned; addresses on a
    /// network no longer configured are judged on every pass.
    /// </summary>
    public void SweepAddresses(DateTime passStartUtc, IReadOnlyCollection<string>? coveredSubnets,
        int missThreshold, IReadOnlyCollection<string>? configuredSubnets = null)
    {
        if (missThreshold < 1) missThreshold = 1;
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays).ToString("o");
        lock (_lock)
        {
            using var conn = Open();

            using (var cmd = conn.CreateCommand())
            {
                var scope = "";
                if (coveredSubnets is not null)
                {
                    var where = new List<string>();
                    var i = 0;
                    foreach (var s in coveredSubnets) cmd.Parameters.AddWithValue($"$c{i++}", s);
                    if (i > 0)
                        where.Add($"subnet IN ({string.Join(", ", Enumerable.Range(0, i).Select(n => $"$c{n}"))})");
                    if (configuredSubnets is { Count: > 0 })
                    {
                        var j = 0;
                        foreach (var s in configuredSubnets) cmd.Parameters.AddWithValue($"$k{j++}", s);
                        where.Add($"subnet NOT IN ({string.Join(", ", Enumerable.Range(0, j).Select(n => $"$k{n}"))})");
                    }
                    if (where.Count == 0) return;
                    scope = $" AND ({string.Join(" OR ", where)})";
                }
                cmd.CommandText = $"""
                    UPDATE host_addresses
                    SET misses = misses + 1,
                        current = CASE WHEN misses + 1 >= $t THEN 0 ELSE 1 END
                    WHERE current = 1 AND last_seen < $start{scope}
                    """;
                cmd.Parameters.AddWithValue("$t", missThreshold);
                cmd.Parameters.AddWithValue("$start", passStartUtc.ToString("o"));
                cmd.ExecuteNonQuery();
            }

            // Devices whose main address stopped answering but that are still
            // at another one: that one becomes the main address. Prefer one on
            // the same network, then the most recently seen.
            var retired = new List<(long Id, string Subnet)>();
            using (var find = conn.CreateCommand())
            {
                find.CommandText = """
                    SELECT h.id, h.subnet FROM hosts h
                    JOIN host_addresses p ON p.host_id = h.id AND p.ip = h.ip AND p.current = 0
                    """;
                using var r = find.ExecuteReader();
                while (r.Read()) retired.Add((r.GetInt64(0), r.GetString(1)));
            }
            var moves = new List<(long Id, string Ip, string Subnet)>();
            foreach (var (id, oldSubnet) in retired)
            {
                using var pick = conn.CreateCommand();
                pick.CommandText = """
                    SELECT ip, subnet FROM host_addresses WHERE host_id = $id AND current = 1
                    ORDER BY (subnet = $subnet) DESC, last_seen DESC LIMIT 1
                    """;
                pick.Parameters.AddWithValue("$id", id);
                pick.Parameters.AddWithValue("$subnet", oldSubnet);
                using var r = pick.ExecuteReader();
                if (r.Read()) moves.Add((id, r.GetString(0), r.GetString(1)));
            }
            var now = DateTime.UtcNow.ToString("o");
            foreach (var (id, ip, subnet) in moves)
            {
                using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE hosts SET ip = $ip, subnet = $subnet WHERE id = $id";
                upd.Parameters.AddWithValue("$ip", ip);
                upd.Parameters.AddWithValue("$subnet", subnet);
                upd.Parameters.AddWithValue("$id", id);
                upd.ExecuteNonQuery();
                AddIpChange(conn, id, ip, now);
            }

            // Old addresses age out with the rest of the history.
            using (var prune = conn.CreateCommand())
            {
                prune.CommandText = "DELETE FROM host_addresses WHERE current = 0 AND last_seen < $cutoff";
                prune.Parameters.AddWithValue("$cutoff", cutoff);
                prune.ExecuteNonQuery();
            }
        }
    }

    /// <summary>host id -> its addresses, main address first, then current ones by address.</summary>
    public Dictionary<long, List<HostAddress>> GetAddresses()
    {
        lock (_lock)
        {
            using var conn = Open();
            var map = new Dictionary<long, List<HostAddress>>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT a.host_id, a.ip, a.subnet, a.first_seen, a.last_seen, a.current, (a.ip = h.ip)
                FROM host_addresses a JOIN hosts h ON h.id = a.host_id
                """;
            var rows = new List<(long Id, HostAddress A, bool Main)>();
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    rows.Add((r.GetInt64(0),
                        new HostAddress(r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5) == 1),
                        r.GetInt64(6) == 1));
            foreach (var g in rows.GroupBy(x => x.Id))
                map[g.Key] = g
                    .OrderByDescending(x => x.Main)
                    .ThenByDescending(x => x.A.Current)
                    .ThenBy(x => IpKey(x.A.Ip))
                    .Select(x => x.A).ToList();
            return map;
        }
    }

    private static long IpKey(string ip) =>
        System.Net.IPAddress.TryParse(ip, out var a) && a.GetAddressBytes() is { Length: 4 } b
            ? ((long)b[0] << 24) | ((long)b[1] << 16) | ((long)b[2] << 8) | b[3]
            : long.MaxValue;

    private static void ForgetAddresses(SqliteConnection conn, long hostId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM host_addresses WHERE host_id = $id";
        cmd.Parameters.AddWithValue("$id", hostId);
        cmd.ExecuteNonQuery();
    }
}
