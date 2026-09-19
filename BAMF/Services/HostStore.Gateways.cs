using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>A device the user declared the gateway of a network, at one of its addresses.</summary>
public record DeclaredGateway(string Subnet, long HostId, string Ip);

/// <summary>
/// Gateways the user declared. BAMF's own idea of a network's gateway comes
/// from this machine's routing table, which only knows the default gateway of
/// networks the machine has an interface on with a route through it. A router
/// with an address on each of your networks is the gateway of every one, and
/// only you can say so. One gateway per network: declaring another replaces it.
/// </summary>
public partial class HostStore
{
    private static void InitGateways(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS gateways (
                subnet  TEXT PRIMARY KEY,
                host_id INTEGER NOT NULL,
                ip      TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public List<DeclaredGateway> GetGateways()
    {
        lock (_lock)
        {
            using var conn = Open();
            return GetGatewaysInternal(conn);
        }
    }

    private static List<DeclaredGateway> GetGatewaysInternal(SqliteConnection conn)
    {
        var list = new List<DeclaredGateway>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT subnet, host_id, ip FROM gateways ORDER BY subnet";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new DeclaredGateway(r.GetString(0), r.GetInt64(1), r.GetString(2)));
        return list;
    }

    /// <summary>
    /// Declares (or, with <paramref name="enabled"/> false, stops declaring) a
    /// device the gateway of the network one of its addresses is on. Returns an
    /// error for the user, or null.
    /// </summary>
    public string? SetGateway(long hostId, string? ip, bool enabled)
    {
        ip = (ip ?? "").Trim();
        lock (_lock)
        {
            using var conn = Open();
            if (!enabled)
            {
                // Always allowed, even for an address the device no longer answers on.
                using var drop = conn.CreateCommand();
                drop.CommandText = "DELETE FROM gateways WHERE host_id = $h AND ip = $ip";
                drop.Parameters.AddWithValue("$h", hostId);
                drop.Parameters.AddWithValue("$ip", ip);
                drop.ExecuteNonQuery();
                return null;
            }
            var host = GetByIdInternal(conn, hostId);
            if (host is null) return "That device no longer exists.";
            // The address has to be one the device has: its main one, or one of its others.
            string? subnet = host.Ip == ip ? host.Subnet : null;
            using (var find = conn.CreateCommand())
            {
                // Its own, or one of a network card combined into it.
                find.CommandText = """
                    SELECT subnet FROM host_addresses WHERE ip = $ip
                      AND (host_id = $h OR host_id IN (SELECT host_id FROM host_interfaces WHERE parent_id = $h))
                    ORDER BY host_id = $h DESC LIMIT 1
                    """;
                find.Parameters.AddWithValue("$h", hostId);
                find.Parameters.AddWithValue("$ip", ip);
                if (find.ExecuteScalar() is string s && s.Length > 0) subnet ??= s;
            }
            if (subnet is null) return $"{(ip.Length == 0 ? "That address" : ip)} isn't one of this device's addresses.";
            if (subnet.Length == 0) return $"BAMF doesn't know which network {ip} is on.";

            using var cmd = conn.CreateCommand();
            var vpn = GetSwitchesInternal(conn).FirstOrDefault(s => s.HostId == hostId && s.Kind == "vpn");
            if (vpn is not null)
                return $"This device is the VPN \"{vpn.Name}\". A VPN is never a network's gateway.";
            cmd.CommandText = """
                INSERT INTO gateways (subnet, host_id, ip) VALUES ($s, $h, $ip)
                ON CONFLICT(subnet) DO UPDATE SET host_id = $h, ip = $ip
                """;
            cmd.Parameters.AddWithValue("$s", subnet);
            cmd.Parameters.AddWithValue("$h", hostId);
            cmd.Parameters.AddWithValue("$ip", ip);
            cmd.ExecuteNonQuery();
            return null;
        }
    }
}
