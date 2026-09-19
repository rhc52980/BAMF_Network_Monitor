using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// Names read from the router (see RouterImport), and which addresses on a
/// network have been in use lately, for the free-address helper.
/// </summary>
public partial class HostStore
{
    private static void InitHelpers(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS router_names (
                mac     TEXT PRIMARY KEY,
                name    TEXT NOT NULL,
                ip      TEXT NOT NULL DEFAULT '',
                source  TEXT NOT NULL DEFAULT '',
                seen_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Replaces what the router last said: a device it no longer lists keeps its name until the next import that does.</summary>
    public void SaveRouterNames(IEnumerable<(string Mac, string Name, string Ip)> names, string source)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            var now = DateTime.UtcNow.ToString("o");
            foreach (var (mac, name, ip) in names)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO router_names (mac, name, ip, source, seen_at) VALUES ($m, $n, $ip, $s, $at)
                    ON CONFLICT(mac) DO UPDATE SET name = excluded.name, ip = excluded.ip, source = excluded.source, seen_at = excluded.seen_at
                    """;
                cmd.Parameters.AddWithValue("$m", mac);
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$ip", ip);
                cmd.Parameters.AddWithValue("$s", source);
                cmd.Parameters.AddWithValue("$at", now);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public Dictionary<string, string> GetRouterNames()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT mac, name FROM router_names";
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetString(0)] = r.GetString(1);
            return map;
        }
    }

    /// <summary>
    /// Copies router names into BAMF's own names. Only devices with no name of
    /// their own, unless overwrite. Returns how many it named.
    /// </summary>
    public int ApplyRouterNames(bool overwrite)
    {
        var names = GetRouterNames();
        var n = 0;
        foreach (var h in GetAll())
        {
            if (!names.TryGetValue(h.Mac, out var name) || name == "") continue;
            if (!overwrite && h.CustomName != "") continue;
            if (h.CustomName == name) continue;
            if (SetName(h.Id, name)) n++;
        }
        return n;
    }

    /// <summary>
    /// Every address on a network that a device has used since the given
    /// time, plus each device's current address whatever its age: the ones a
    /// new static address shouldn't take.
    /// </summary>
    public HashSet<string> UsedAddresses(string subnet, DateTime sinceUtc)
    {
        lock (_lock)
        {
            using var conn = Open();
            var used = new HashSet<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT ip FROM host_addresses WHERE subnet = $s AND last_seen >= $since";
                cmd.Parameters.AddWithValue("$s", subnet);
                cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("o"));
                using var r = cmd.ExecuteReader();
                while (r.Read()) used.Add(r.GetString(0));
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT ip FROM hosts WHERE subnet = $s AND forgotten = 0 AND ip <> ''";
                cmd.Parameters.AddWithValue("$s", subnet);
                using var r = cmd.ExecuteReader();
                while (r.Read()) used.Add(r.GetString(0));
            }
            return used;
        }
    }
}
