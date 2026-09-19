using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// Devices with more than one network card. Each card has its own MAC, so a
/// scan records each as a device of its own: BAMF's server with a card on each
/// of your networks shows up once per network. Combining says "these are one
/// machine": the extra cards become interfaces of the main record, and the
/// dashboard shows one device that answers on all their addresses. The records
/// themselves are left as they are, so separating again loses nothing.
/// </summary>
public partial class HostStore
{
    private static void InitInterfaces(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS host_interfaces (
                host_id   INTEGER PRIMARY KEY,
                parent_id INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>host id -> the device it's an extra network card of.</summary>
    public Dictionary<long, long> GetInterfaces()
    {
        lock (_lock)
        {
            using var conn = Open();
            var map = new Dictionary<long, long>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT host_id, parent_id FROM host_interfaces";
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetInt64(0)] = r.GetInt64(1);
            return map;
        }
    }

    /// <summary>
    /// Makes a device an extra network card of another (parent 0 separates it
    /// again). Returns an error for the user, or null.
    /// </summary>
    public string? SetInterfaceOf(long hostId, long parentId)
    {
        lock (_lock)
        {
            using var conn = Open();
            if (GetByIdInternal(conn, hostId) is null) return "That device no longer exists.";
            using var cmd = conn.CreateCommand();
            if (parentId <= 0)
            {
                cmd.CommandText = "DELETE FROM host_interfaces WHERE host_id = $h";
                cmd.Parameters.AddWithValue("$h", hostId);
                cmd.ExecuteNonQuery();
                return null;
            }
            // Combining onto a card that's itself combined means onto its device.
            using (var up = conn.CreateCommand())
            {
                up.CommandText = "SELECT parent_id FROM host_interfaces WHERE host_id = $p";
                up.Parameters.AddWithValue("$p", parentId);
                if (up.ExecuteScalar() is long grand) parentId = grand;
            }
            if (parentId == hostId) return "A device can't be combined with itself.";
            if (GetByIdInternal(conn, parentId) is null) return "The device to combine it with no longer exists.";
            var sw = GetSwitchesInternal(conn).FirstOrDefault(s => s.HostId == hostId);
            if (sw is not null)
                return $"This device is the {KindNoun(sw.Kind)} \"{sw.Name}\". Combine the other way round: open that device and add this one to it.";

            if (GetSwitchesInternal(conn).FirstOrDefault(s => s.HostId == parentId && s.Kind == "vpn") is { } vpn)
            {
                using var gw = conn.CreateCommand();
                gw.CommandText = "SELECT COUNT(*) FROM gateways WHERE host_id = $h";
                gw.Parameters.AddWithValue("$h", hostId);
                if (Convert.ToInt64(gw.ExecuteScalar()) > 0)
                    return $"This device is declared a gateway, and \"{vpn.Name}\" is a VPN. A VPN is never a network's gateway.";
            }

            using var tx = conn.BeginTransaction();
            cmd.Transaction = tx;
            // Its own extra cards come along; where it's plugged in and what
            // it's the gateway of now belong to the device it joins.
            cmd.CommandText = """
                UPDATE host_interfaces SET parent_id = $p WHERE parent_id = $h;
                INSERT INTO host_interfaces (host_id, parent_id) VALUES ($h, $p)
                    ON CONFLICT(host_id) DO UPDATE SET parent_id = $p;
                DELETE FROM placements WHERE host_id = $h;
                UPDATE OR REPLACE gateways SET host_id = $p WHERE host_id = $h;
                UPDATE switches SET runs_on = $p WHERE runs_on = $h AND kind = 'virtual';
                """;
            cmd.Parameters.AddWithValue("$h", hostId);
            cmd.Parameters.AddWithValue("$p", parentId);
            cmd.ExecuteNonQuery();
            tx.Commit();
            return null;
        }
    }
}
