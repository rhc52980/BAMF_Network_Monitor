using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// The quieter changes to a device, which no alert used to notice: its name changing, its moving to another network, and a different
/// device taking over an address that one still online was answering at a moment ago. Each is written down when a scan finds it, shows
/// in the device's timeline and in What changed, and the takeover, which is what a spoofed or cloned device looks like from outside,
/// can alert. The IP conflict the ARP watch raises is two devices at one address in the same scan; this is the one that replaced
/// the other between scans.
/// </summary>
public partial class HostStore
{
    /// <summary>One change: kind is "name", "network" or "takeover"; for a takeover, Before is the device it took the address from and After the address.</summary>
    public sealed record Drift(long HostId, string At, string Kind, string Before, string After);

    private readonly List<Drift> _driftThisCycle = new();

    private static void InitDrift(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS host_changes (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                host_id INTEGER NOT NULL,
                at      TEXT NOT NULL,
                kind    TEXT NOT NULL,
                before  TEXT NOT NULL DEFAULT '',
                after   TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_host_changes_host ON host_changes(host_id, at DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    private void AddDrift(SqliteConnection conn, long hostId, string kind, string before, string after, string at)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO host_changes (host_id, at, kind, before, after) VALUES ($h, $at, $k, $b, $a)";
        cmd.Parameters.AddWithValue("$h", hostId);
        cmd.Parameters.AddWithValue("$at", at);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$b", before);
        cmd.Parameters.AddWithValue("$a", after);
        cmd.ExecuteNonQuery();
        lock (_recoveredLock) _driftThisCycle.Add(new Drift(hostId, at, kind, before, after));
    }

    /// <summary>
    /// Another device, still online, whose main address this one has just been found at: that device's id, and how to describe it.
    /// Null when the address was free, when the other device has already been marked offline (an address a DHCP lease handed on
    /// is nobody's doing), or when the two are the same kind of thing by vendor (a phone with a fresh private address taking the
    /// address its old one had).
    /// </summary>
    private static (long Id, string Who)? TakenFrom(SqliteConnection conn, string mac, string vendor, string ip)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, mac, hostname, custom_name, vendor FROM hosts
            WHERE ip = $ip AND mac <> $mac AND online = 1 AND forgotten = 0 AND ignored = 0
            ORDER BY last_seen DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$ip", ip);
        cmd.Parameters.AddWithValue("$mac", mac);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var otherVendor = r.GetString(4);
        if (string.Equals(otherVendor, vendor, StringComparison.OrdinalIgnoreCase)) return null;
        var name = r.GetString(3) != "" ? r.GetString(3) : r.GetString(2) != "" ? r.GetString(2) : r.GetString(1);
        return (r.GetInt64(0), $"{name} ({r.GetString(1)}{(otherVendor != "" ? ", " + otherVendor : "")})");
    }

    /// <summary>Returns and clears the changes scans have found since the last drain, for the scanner to alert on.</summary>
    public List<Drift> DrainDrift()
    {
        lock (_recoveredLock)
        {
            var list = _driftThisCycle.ToList();
            _driftThisCycle.Clear();
            return list;
        }
    }

    /// <summary>A device's changes, newest first.</summary>
    public List<Drift> GetDrift(long hostId, int limit = 50)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT host_id, at, kind, before, after FROM host_changes WHERE host_id = $h ORDER BY at DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$h", hostId);
            cmd.Parameters.AddWithValue("$n", limit);
            return ReadDrift(cmd);
        }
    }

    /// <summary>Every device's changes since a moment, newest first.</summary>
    public List<Drift> DriftSince(DateTime sinceUtc)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT c.host_id, c.at, c.kind, c.before, c.after FROM host_changes c JOIN hosts h ON h.id = c.host_id
                WHERE c.at >= $since AND h.forgotten = 0 AND h.ignored = 0 ORDER BY c.at DESC LIMIT 200
                """;
            cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("o"));
            return ReadDrift(cmd);
        }
    }

    private static List<Drift> ReadDrift(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<Drift>();
        while (r.Read()) list.Add(new Drift(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    /// <summary>A change as a line of a device's story.</summary>
    public static string DriftText(Drift d) => d.Kind switch
    {
        "name" => $"Name changed to {d.After} (was {d.Before})",
        "network" => $"Moved to {d.After} (was on {d.Before})",
        "takeover" => $"Took over {d.After} from {d.Before}, which was still online",
        _ => $"{d.Kind}: {d.Before} → {d.After}",
    };
}
