using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// A switch the user has described. BAMF can't discover cabling: ARP shows who
/// is on a network, not which port they are plugged into, and budget "smart"
/// switches don't expose their MAC table. So this is the user's own account of
/// the layout, and the map draws it as that.
/// </summary>
/// <param name="HostId">The BAMF device that is this switch, when it has an address BAMF sees; 0 otherwise.</param>
/// <param name="Uplink">What the switch is plugged into: "" not recorded, "router", or "switch".</param>
/// <param name="UplinkSwitch">For a "switch" uplink, the parent switch's id.</param>
/// <param name="UplinkPort">For a "switch" uplink, the parent's port; 0 when not recorded.</param>
/// <param name="Kind">"switch", "router" or "ap" (access point). All have ports and
/// work the same way; the kind sets the Map's icon, and a router linked to the
/// network's gateway becomes the top of the topology.</param>
public record SwitchRecord(
    long Id, string Name, int Ports, string Subnet, long HostId,
    string Uplink, long UplinkSwitch, int UplinkPort, string Kind);

public record SwitchInput(
    string? Name, int Ports, string? Subnet, long HostId,
    string? Uplink, long UplinkSwitch, int UplinkPort, string? Kind);

public partial class HostStore
{
    public const int MaxSwitchPorts = 128;
    public static readonly string[] SwitchKinds = { "switch", "router", "ap" };

    private static string KindNoun(string kind) => kind switch { "router" => "router", "ap" => "access point", _ => "switch" };

    private static void InitSwitches(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS switches (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                name          TEXT NOT NULL,
                ports         INTEGER NOT NULL,
                subnet        TEXT NOT NULL DEFAULT '',
                host_id       INTEGER NOT NULL DEFAULT 0,
                uplink        TEXT NOT NULL DEFAULT '',
                uplink_switch INTEGER NOT NULL DEFAULT 0,
                uplink_port   INTEGER NOT NULL DEFAULT 0
            );
            -- Which switch port a device is plugged into, as the user recorded it.
            -- Its own table so the hosts row, read on every scan, stays as it was.
            CREATE TABLE IF NOT EXISTS placements (
                host_id   INTEGER PRIMARY KEY,
                switch_id INTEGER NOT NULL,
                port      INTEGER NOT NULL DEFAULT 0
            );
            -- Where each port's cable goes, e.g. "Living Room". Belongs to the
            -- port, not the device, so it stays when a device moves.
            CREATE TABLE IF NOT EXISTS port_labels (
                switch_id INTEGER NOT NULL,
                port      INTEGER NOT NULL,
                label     TEXT NOT NULL,
                PRIMARY KEY (switch_id, port)
            );
            """;
        cmd.ExecuteNonQuery();

        // Added in 1.27: what kind of box it is. Everything recorded before
        // then was a switch.
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(switches)";
            using var r = info.ExecuteReader();
            while (r.Read()) cols.Add(r.GetString(1));
        }
        if (!cols.Contains("kind"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE switches ADD COLUMN kind TEXT NOT NULL DEFAULT 'switch'";
            alter.ExecuteNonQuery();
        }
    }

    public List<SwitchRecord> GetSwitches()
    {
        lock (_lock)
        {
            using var conn = Open();
            return GetSwitchesInternal(conn);
        }
    }

    private static List<SwitchRecord> GetSwitchesInternal(SqliteConnection conn)
    {
        var list = new List<SwitchRecord>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, ports, subnet, host_id, uplink, uplink_switch, uplink_port, kind FROM switches ORDER BY name COLLATE NOCASE, id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SwitchRecord(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3),
                r.GetInt64(4), r.GetString(5), r.GetInt64(6), r.GetInt32(7), r.GetString(8)));
        return list;
    }

    public const int MaxPortLabel = 40;

    /// <summary>switch id -> port -> location label.</summary>
    public Dictionary<long, Dictionary<int, string>> GetPortLabels()
    {
        lock (_lock)
        {
            using var conn = Open();
            var map = new Dictionary<long, Dictionary<int, string>>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT switch_id, port, label FROM port_labels";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!map.TryGetValue(r.GetInt64(0), out var ports)) map[r.GetInt64(0)] = ports = new();
                ports[r.GetInt32(1)] = r.GetString(2);
            }
            return map;
        }
    }

    /// <summary>host id -> (switch id, port), port 0 meaning "port not recorded".</summary>
    public Dictionary<long, (long SwitchId, int Port)> GetPlacements()
    {
        lock (_lock)
        {
            using var conn = Open();
            return GetPlacementsInternal(conn);
        }
    }

    private static Dictionary<long, (long, int)> GetPlacementsInternal(SqliteConnection conn)
    {
        var map = new Dictionary<long, (long, int)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT host_id, switch_id, port FROM placements";
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt64(0)] = (r.GetInt64(1), r.GetInt32(2));
        return map;
    }

    /// <summary>
    /// Creates (id null) or updates a switch. Returns the saved record, or an
    /// error message suitable for showing the user.
    /// </summary>
    public (SwitchRecord? Saved, string? Error) SaveSwitch(long? id, SwitchInput input)
    {
        lock (_lock)
        {
            using var conn = Open();
            var all = GetSwitchesInternal(conn);
            if (id is long existingId && all.All(s => s.Id != existingId)) return (null, "That switch no longer exists.");

            var kind = (input.Kind ?? "switch").Trim().ToLowerInvariant();
            if (!SwitchKinds.Contains(kind)) kind = "switch";
            var name = (input.Name ?? "").Trim();
            if (name.Length == 0) return (null, $"Give the {KindNoun(kind)} a name.");
            if (name.Length > 60) name = name[..60];
            if (input.Ports < 1 || input.Ports > MaxSwitchPorts) return (null, $"Ports must be between 1 and {MaxSwitchPorts}.");

            var subnet = (input.Subnet ?? "").Trim();
            long hostId = Math.Max(0, input.HostId);
            if (hostId > 0)
            {
                var host = GetByIdInternal(conn, hostId);
                if (host is null) return (null, "That device no longer exists.");
                var other = all.FirstOrDefault(s => s.HostId == hostId && s.Id != id);
                if (other is not null) return (null, $"That device is already the {KindNoun(other.Kind)} \"{other.Name}\".");
                subnet = host.Subnet;   // a switch BAMF can see lives where BAMF sees it
            }

            var uplink = (input.Uplink ?? "").Trim().ToLowerInvariant();
            long uplinkSwitch = 0; int uplinkPort = 0;
            if (uplink == "switch")
            {
                var parent = all.FirstOrDefault(s => s.Id == input.UplinkSwitch);
                if (parent is null) return (null, "The switch it's plugged into no longer exists.");
                if (id is long self)
                {
                    // Walk up from the proposed parent; meeting this switch means a loop.
                    for (var p = parent; p is not null; p = p.Uplink == "switch" ? all.FirstOrDefault(s => s.Id == p.UplinkSwitch) : null)
                        if (p.Id == self) return (null, "A switch can't be plugged into itself or into a switch below it.");
                }
                if (input.UplinkPort < 0 || input.UplinkPort > parent.Ports)
                    return (null, $"\"{parent.Name}\" has ports 1 to {parent.Ports}.");
                uplinkSwitch = parent.Id;
                uplinkPort = input.UplinkPort;
            }
            else if (uplink != "router") uplink = "";

            if (id is long shrinking)
            {
                using var over = conn.CreateCommand();
                over.CommandText = """
                    SELECT (SELECT COUNT(*) FROM placements WHERE switch_id = $id AND port > $p)
                         + (SELECT COUNT(*) FROM switches WHERE uplink = 'switch' AND uplink_switch = $id AND uplink_port > $p)
                    """;
                over.Parameters.AddWithValue("$id", shrinking);
                over.Parameters.AddWithValue("$p", input.Ports);
                var n = Convert.ToInt32(over.ExecuteScalar());
                if (n > 0) return (null, $"{n} device(s) are recorded on ports above {input.Ports}. Move them first.");
            }

            using var tx = conn.BeginTransaction();
            long savedId;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = id is null
                    ? """
                      INSERT INTO switches (name, ports, subnet, host_id, uplink, uplink_switch, uplink_port, kind)
                      VALUES ($n, $p, $s, $h, $u, $us, $up, $k);
                      SELECT last_insert_rowid();
                      """
                    : """
                      UPDATE switches SET name = $n, ports = $p, subnet = $s, host_id = $h,
                             uplink = $u, uplink_switch = $us, uplink_port = $up, kind = $k
                      WHERE id = $id;
                      SELECT $id;
                      """;
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$p", input.Ports);
                cmd.Parameters.AddWithValue("$s", subnet);
                cmd.Parameters.AddWithValue("$h", hostId);
                cmd.Parameters.AddWithValue("$u", uplink);
                cmd.Parameters.AddWithValue("$us", uplinkSwitch);
                cmd.Parameters.AddWithValue("$up", uplinkPort);
                cmd.Parameters.AddWithValue("$k", kind);
                if (id is not null) cmd.Parameters.AddWithValue("$id", id.Value);
                savedId = Convert.ToInt64(cmd.ExecuteScalar());
            }
            if (hostId > 0)
            {
                // The switch's own device is placed by the switch's uplink, not as
                // a device on a port, so drop any placement it had.
                using var drop = conn.CreateCommand();
                drop.Transaction = tx;
                drop.CommandText = "DELETE FROM placements WHERE host_id = $h";
                drop.Parameters.AddWithValue("$h", hostId);
                drop.ExecuteNonQuery();
            }
            tx.Commit();
            return (GetSwitchesInternal(conn).First(s => s.Id == savedId), null);
        }
    }

    /// <summary>
    /// Deletes a switch. Devices recorded on it become unplaced again, and
    /// switches plugged into it lose that uplink; nothing else is touched.
    /// </summary>
    public bool DeleteSwitch(long id)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM placements WHERE switch_id = $id;
                DELETE FROM port_labels WHERE switch_id = $id;
                UPDATE switches SET uplink = '', uplink_switch = 0, uplink_port = 0
                    WHERE uplink = 'switch' AND uplink_switch = $id;
                DELETE FROM switches WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            // changes() counts the last statement only - the switch's own
            // DELETE - so read it before anything else runs on this connection.
            using var check = conn.CreateCommand();
            check.Transaction = tx;
            check.CommandText = "SELECT changes()";
            var deleted = Convert.ToInt64(check.ExecuteScalar()) > 0;
            ForgetMapNode(conn, $"s:{id}", tx);
            tx.Commit();
            return deleted;
        }
    }

    /// <summary>
    /// Records which switch port a device is plugged into. Switch id 0 clears
    /// it; port 0 means "on this switch, port not recorded".
    /// </summary>
    public string? SetPlacement(long hostId, long switchId, int port)
    {
        lock (_lock)
        {
            using var conn = Open();
            if (GetByIdInternal(conn, hostId) is null) return "That device no longer exists.";
            using var cmd = conn.CreateCommand();
            if (switchId <= 0)
            {
                cmd.CommandText = "DELETE FROM placements WHERE host_id = $h";
                cmd.Parameters.AddWithValue("$h", hostId);
                cmd.ExecuteNonQuery();
                return null;
            }
            var all = GetSwitchesInternal(conn);
            var sw = all.FirstOrDefault(s => s.Id == switchId);
            if (sw is null) return "That switch no longer exists.";
            var own = all.FirstOrDefault(s => s.HostId == hostId);
            if (own is not null) return $"This device is the switch \"{own.Name}\". Set what it's plugged into in that switch's settings.";
            if (port < 0 || port > sw.Ports) return $"\"{sw.Name}\" has ports 1 to {sw.Ports}.";

            cmd.CommandText = """
                INSERT INTO placements (host_id, switch_id, port) VALUES ($h, $s, $p)
                ON CONFLICT(host_id) DO UPDATE SET switch_id = $s, port = $p
                """;
            cmd.Parameters.AddWithValue("$h", hostId);
            cmd.Parameters.AddWithValue("$s", switchId);
            cmd.Parameters.AddWithValue("$p", port);
            cmd.ExecuteNonQuery();
            return null;
        }
    }

    /// <summary>
    /// Replaces everything recorded on one switch in a single step, from its
    /// Ports dialog. Hosts listed are placed on it at the given port (0 = port
    /// not recorded), moving off any other switch; hosts on it that aren't
    /// listed are unplaced. Returns an error for the user, or null.
    /// </summary>
    /// <param name="labels">When not null, replaces the location labels on
    /// the switch's ports; blank labels are dropped. Null leaves them alone.</param>
    public string? SetSwitchPorts(long switchId, IReadOnlyList<(long HostId, int Port)> entries,
        IReadOnlyList<(int Port, string Label)>? labels = null)
    {
        lock (_lock)
        {
            using var conn = Open();
            var all = GetSwitchesInternal(conn);
            var sw = all.FirstOrDefault(s => s.Id == switchId);
            if (sw is null) return "That switch no longer exists.";
            var seen = new HashSet<long>();
            foreach (var (hostId, port) in entries)
            {
                if (!seen.Add(hostId)) return "The same device is listed on two ports.";
                var host = GetByIdInternal(conn, hostId);
                if (host is null) return "A device in the list no longer exists.";
                var own = all.FirstOrDefault(s => s.HostId == hostId);
                if (own is not null) return $"\"{own.Name}\" is a switch. Plug it in from its own settings.";
                if (port < 0 || port > sw.Ports) return $"\"{sw.Name}\" has ports 1 to {sw.Ports}.";
            }
            var cleanLabels = new Dictionary<int, string>();
            foreach (var (port, label) in labels ?? Array.Empty<(int, string)>())
            {
                if (port < 1 || port > sw.Ports) return $"\"{sw.Name}\" has ports 1 to {sw.Ports}.";
                var text = (label ?? "").Trim();
                if (text.Length > MaxPortLabel) text = text[..MaxPortLabel];
                if (text.Length > 0) cleanLabels[port] = text;
            }

            using var tx = conn.BeginTransaction();
            using (var clear = conn.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM placements WHERE switch_id = $s";
                clear.Parameters.AddWithValue("$s", switchId);
                clear.ExecuteNonQuery();
            }
            foreach (var (hostId, port) in entries)
            {
                using var put = conn.CreateCommand();
                put.Transaction = tx;
                put.CommandText = """
                    INSERT INTO placements (host_id, switch_id, port) VALUES ($h, $s, $p)
                    ON CONFLICT(host_id) DO UPDATE SET switch_id = $s, port = $p
                    """;
                put.Parameters.AddWithValue("$h", hostId);
                put.Parameters.AddWithValue("$s", switchId);
                put.Parameters.AddWithValue("$p", port);
                put.ExecuteNonQuery();
            }
            if (labels is not null)
            {
                // Only the ports the dialog showed are replaced: labels above a
                // lowered port count are kept, and come back if it's raised again.
                using (var drop = conn.CreateCommand())
                {
                    drop.Transaction = tx;
                    drop.CommandText = "DELETE FROM port_labels WHERE switch_id = $s AND port <= $n";
                    drop.Parameters.AddWithValue("$s", switchId);
                    drop.Parameters.AddWithValue("$n", sw.Ports);
                    drop.ExecuteNonQuery();
                }
                foreach (var (port, text) in cleanLabels)
                {
                    using var put = conn.CreateCommand();
                    put.Transaction = tx;
                    put.CommandText = "INSERT INTO port_labels (switch_id, port, label) VALUES ($s, $p, $l)";
                    put.Parameters.AddWithValue("$s", switchId);
                    put.Parameters.AddWithValue("$p", port);
                    put.Parameters.AddWithValue("$l", text);
                    put.ExecuteNonQuery();
                }
            }
            tx.Commit();
            return null;
        }
    }

    /// <summary>Removes what the switch layout says about a host that is being deleted for good.</summary>
    private static void ForgetHostInLayout(SqliteConnection conn, long hostId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM placements WHERE host_id = $id;
            UPDATE switches SET host_id = 0 WHERE host_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", hostId);
        cmd.ExecuteNonQuery();
    }
}
