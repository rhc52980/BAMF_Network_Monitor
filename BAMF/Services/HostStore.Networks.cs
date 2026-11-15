namespace LanWatch.Services;

/// <summary>
/// What a network label holds, and moving or forgetting it: the devices under
/// a network are kept when the network comes off the scan list, so changing a
/// network's range, or taking it away, has to say what happens to them.
/// </summary>
public partial class HostStore
{
    /// <summary>A device's address and whether it is online, for the ones filed under a network.</summary>
    public record NetworkDevice(long Id, string Ip, bool Online, bool Forgotten);

    /// <summary>Every device filed under a network, by its main address; forgotten ones too, marked.</summary>
    public List<NetworkDevice> DevicesOn(string network)
    {
        var list = new List<NetworkDevice>();
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, ip, online, forgotten FROM hosts WHERE subnet = $s";
            cmd.Parameters.AddWithValue("$s", network);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new NetworkDevice(r.GetInt64(0), r.GetString(1), r.GetInt32(2) != 0, r.GetInt32(3) != 0));
        }
        return list;
    }

    /// <summary>
    /// Files the devices under one network under another: those whose address is
    /// inside it, which is all of them when a /22 widens to a /21. Their address
    /// history, a declared gateway and the map's saved positions follow.
    /// Returns how many moved.
    /// </summary>
    public int MoveNetwork(string from, string to, Func<string, bool> fits)
    {
        if (from == to) return 0;
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            var ids = new List<long>();
            using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT id, ip FROM hosts WHERE subnet = $f";
                q.Parameters.AddWithValue("$f", from);
                using var r = q.ExecuteReader();
                while (r.Read()) if (fits(r.GetString(1))) ids.Add(r.GetInt64(0));
            }
            foreach (var id in ids)
            {
                using var u = conn.CreateCommand();
                u.Transaction = tx;
                u.CommandText = "UPDATE hosts SET subnet = $t WHERE id = $id";
                u.Parameters.AddWithValue("$t", to);
                u.Parameters.AddWithValue("$id", id);
                u.ExecuteNonQuery();
            }
            // Address history: each address that fits, whichever device it belongs to.
            var addrs = new List<(long Host, string Ip)>();
            using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT host_id, ip FROM host_addresses WHERE subnet = $f";
                q.Parameters.AddWithValue("$f", from);
                using var r = q.ExecuteReader();
                while (r.Read()) if (fits(r.GetString(1))) addrs.Add((r.GetInt64(0), r.GetString(1)));
            }
            foreach (var (host, ip) in addrs)
            {
                using var u = conn.CreateCommand();
                u.Transaction = tx;
                u.CommandText = "UPDATE host_addresses SET subnet = $t WHERE host_id = $h AND ip = $ip";
                u.Parameters.AddWithValue("$t", to);
                u.Parameters.AddWithValue("$h", host);
                u.Parameters.AddWithValue("$ip", ip);
                u.ExecuteNonQuery();
            }
            // A declared gateway keeps its place, unless the new network already has one.
            using (var g = conn.CreateCommand())
            {
                g.Transaction = tx;
                g.CommandText = "SELECT ip FROM gateways WHERE subnet = $f";
                g.Parameters.AddWithValue("$f", from);
                if (g.ExecuteScalar() is string gwIp && fits(gwIp))
                {
                    using var u = conn.CreateCommand();
                    u.Transaction = tx;
                    u.CommandText = "UPDATE OR IGNORE gateways SET subnet = $t WHERE subnet = $f";
                    u.Parameters.AddWithValue("$t", to);
                    u.Parameters.AddWithValue("$f", from);
                    u.ExecuteNonQuery();
                    using var d = conn.CreateCommand();
                    d.Transaction = tx;
                    d.CommandText = "DELETE FROM gateways WHERE subnet = $f";
                    d.Parameters.AddWithValue("$f", from);
                    d.ExecuteNonQuery();
                }
            }
            // Where the map had drawn things; a spot already taken in the new network wins.
            using (var m = conn.CreateCommand())
            {
                m.Transaction = tx;
                m.CommandText = "UPDATE OR IGNORE map_positions SET subnet = $t WHERE subnet = $f";
                m.Parameters.AddWithValue("$t", to);
                m.Parameters.AddWithValue("$f", from);
                m.ExecuteNonQuery();
            }
            tx.Commit();
            return ids.Count;
        }
    }

    /// <summary>Forgets every device filed under a network, as the Forget button does: hidden, kept, and brought back from the Forgotten tab. Returns how many were forgotten now.</summary>
    public int ForgetNetwork(string network)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE hosts SET forgotten = 1, watched = 0 WHERE subnet = $s AND forgotten = 0";
            cmd.Parameters.AddWithValue("$s", network);
            return cmd.ExecuteNonQuery();
        }
    }
}
