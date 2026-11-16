using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// What BAMF learns from the network gear itself (see NetworkGear): the switches, routers and access points
/// it draws on the Map, which port or access point each device is on, how well each Wi-Fi device hears its
/// access point, and the gear's own health. Everything here is off until its switch is turned on, and turning
/// it off takes back what it made.
///
/// What BAMF draws itself is marked "auto" (which source made it, and the key that source knows it by), so
/// it's told apart from what you recorded by hand: a placement or a switch you made yourself is never
/// replaced, and an automatic one you edit becomes yours.
/// </summary>
public partial class HostStore
{
    public sealed record WifiLink(
        long HostId, string ApMac, string ApName, string Ssid, string Band, int Channel,
        int? Signal, int? Noise, int? TxMbps, int? RxMbps, int? Satisfaction, int Roams,
        string Since, string UpdatedAt, string MovedFrom, string MovedAt);

    public sealed record GearRow(
        string Mac, string Source, string Name, string Kind, string Model, string Version, string UpgradeTo,
        long UptimeSeconds, bool Online, string Ip, int PortCount, double? Cpu, double? Mem, double? TempC,
        double? PoeWatts, double? PoeBudget, List<GearPort> Ports, List<GearSsid> Ssids,
        int Reboots, string LastReboot, string FirstSeen, string UpdatedAt);

    public sealed record AutoSwitch(long Id, string Source, string Key);

    // The settings for each piece. All off until switched on.
    public bool GearMapOn => GetSetting("gearMap") == "true";
    public bool GearWifiOn => GetSetting("gearWifi") == "true";
    public bool GearHealthOn => GetSetting("gearHealth") == "true";
    public bool GearVlansOn => GetSetting("gearVlans") == "true";
    public bool GearEventsOn => GetSetting("gearEvents") == "true";
    /// <summary>Blocked threats, and WAN changes: each its own switch, under Gateway events, and off until ticked.</summary>
    public bool GearEventThreats => GearEventsOn && GetSetting("gearEventThreats") == "true";
    public bool GearEventWan => GearEventsOn && GetSetting("gearEventWan") == "true";

    private static void InitGear(SqliteConnection conn)
    {
        static HashSet<string> Columns(SqliteConnection c, string table)
        {
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var info = c.CreateCommand();
            info.CommandText = $"PRAGMA table_info({table})";
            using var r = info.ExecuteReader();
            while (r.Read()) cols.Add(r.GetString(1));
            return cols;
        }
        static void Add(SqliteConnection c, string table, string column, string ddl)
        {
            using var alter = c.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {ddl}";
            alter.ExecuteNonQuery();
        }

        // Which switches and placements BAMF drew itself, as opposed to the ones you recorded.
        var sw = Columns(conn, "switches");
        if (!sw.Contains("auto")) Add(conn, "switches", "auto", "TEXT NOT NULL DEFAULT ''");
        if (!sw.Contains("auto_key")) Add(conn, "switches", "auto_key", "TEXT NOT NULL DEFAULT ''");
        if (!Columns(conn, "placements").Contains("auto")) Add(conn, "placements", "auto", "TEXT NOT NULL DEFAULT ''");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            -- How each Wi-Fi device hears its access point, as of the last read.
            CREATE TABLE IF NOT EXISTS wifi_links (
                host_id      INTEGER PRIMARY KEY,
                ap_mac       TEXT NOT NULL DEFAULT '',
                ap_name      TEXT NOT NULL DEFAULT '',
                ssid         TEXT NOT NULL DEFAULT '',
                band         TEXT NOT NULL DEFAULT '',
                channel      INTEGER NOT NULL DEFAULT 0,
                signal       INTEGER,
                noise        INTEGER,
                tx_mbps      INTEGER,
                rx_mbps      INTEGER,
                satisfaction INTEGER,
                roams        INTEGER NOT NULL DEFAULT 0,
                since        TEXT NOT NULL DEFAULT '',
                updated_at   TEXT NOT NULL DEFAULT '',
                moved_from   TEXT NOT NULL DEFAULT '',
                moved_at     TEXT NOT NULL DEFAULT ''
            );
            -- The gear's own state, keyed by MAC (or "snmp:<switch id>" for a switch with no device).
            CREATE TABLE IF NOT EXISTS gear_health (
                mac         TEXT PRIMARY KEY,
                source      TEXT NOT NULL DEFAULT '',
                name        TEXT NOT NULL DEFAULT '',
                kind        TEXT NOT NULL DEFAULT 'switch',
                model       TEXT NOT NULL DEFAULT '',
                version     TEXT NOT NULL DEFAULT '',
                upgrade_to  TEXT NOT NULL DEFAULT '',
                uptime      INTEGER NOT NULL DEFAULT 0,
                online      INTEGER NOT NULL DEFAULT 1,
                ip          TEXT NOT NULL DEFAULT '',
                ports       INTEGER NOT NULL DEFAULT 0,
                cpu         REAL,
                mem         REAL,
                temp        REAL,
                poe         REAL,
                poe_budget  REAL,
                ports_json  TEXT NOT NULL DEFAULT '[]',
                ssids_json  TEXT NOT NULL DEFAULT '[]',
                reboots     INTEGER NOT NULL DEFAULT 0,
                last_reboot TEXT NOT NULL DEFAULT '',
                first_seen  TEXT NOT NULL DEFAULT '',
                updated_at  TEXT NOT NULL DEFAULT ''
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------ automatic switches

    public List<AutoSwitch> GetAutoSwitches()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, auto, auto_key FROM switches WHERE auto <> '' ORDER BY id";
            var list = new List<AutoSwitch>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new AutoSwitch(r.GetInt64(0), r.GetString(1), r.GetString(2)));
            return list;
        }
    }

    /// <summary>Which source placed each device BAMF placed itself, by host id.</summary>
    public Dictionary<long, string> GetAutoPlacementSources()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT host_id, auto FROM placements WHERE auto <> ''";
            var map = new Dictionary<long, string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetInt64(0)] = r.GetString(1);
            return map;
        }
    }

    /// <summary>How many placements BAMF made itself, rather than you.</summary>
    public int CountAutoPlacements()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM placements WHERE auto <> ''";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>Which source drew each automatic switch, by switch id.</summary>
    public Dictionary<long, string> GetAutoSwitchSources() => GetAutoSwitches().ToDictionary(a => a.Id, a => a.Source);

    /// <summary>
    /// Makes or updates the switch, router, access point or SSID a source reported. Found by the source and
    /// its key, so it keeps one record however often it's read. A record you've edited by hand is no longer
    /// automatic and isn't found again here. Returns its id.
    /// </summary>
    public long UpsertAutoSwitch(string source, string key, string name, string kind, int ports, string subnet, long hostId,
        string uplink, long uplinkSwitch, int uplinkPort, long runsOn)
    {
        lock (_lock)
        {
            using var conn = Open();
            var all = GetSwitchesInternal(conn);
            name = (name ?? "").Trim();
            if (name.Length == 0) name = key;
            if (name.Length > 60) name = name[..60];
            if (!SwitchKinds.Contains(kind)) kind = "switch";
            ports = Math.Clamp(ports, 1, MaxSwitchPorts);

            long existing = 0;
            using (var find = conn.CreateCommand())
            {
                find.CommandText = "SELECT id FROM switches WHERE auto = $s AND auto_key = $k";
                find.Parameters.AddWithValue("$s", source);
                find.Parameters.AddWithValue("$k", key);
                if (find.ExecuteScalar() is long id) existing = id;
            }

            if (existing > 0)
            {
                // Never shrink below a port something is recorded on.
                using var top = conn.CreateCommand();
                top.CommandText = "SELECT COALESCE(MAX(port), 0) FROM placements WHERE switch_id = $id";
                top.Parameters.AddWithValue("$id", existing);
                ports = Math.Max(ports, Convert.ToInt32(top.ExecuteScalar()));
            }

            // What it's plugged into: only a switch that exists and isn't itself below this one.
            if (IsInner(kind) || kind == "vpn") { uplink = ""; uplinkSwitch = 0; uplinkPort = 0; }
            if (uplink == "switch")
            {
                var parent = all.FirstOrDefault(s => s.Id == uplinkSwitch);
                var loop = false;
                for (var p = parent; p is not null && existing > 0; p = p.Uplink == "switch" ? all.FirstOrDefault(s => s.Id == p.UplinkSwitch) : null)
                    if (p.Id == existing) { loop = true; break; }
                if (parent is null || loop || parent.Kind is "virtual" or "ssid" or "vpn") { uplink = ""; uplinkSwitch = 0; uplinkPort = 0; }
                else uplinkPort = uplinkPort < 0 || uplinkPort > parent.Ports ? 0 : uplinkPort;
            }
            else if (uplink != "router") { uplink = ""; uplinkSwitch = 0; uplinkPort = 0; }
            else { uplinkSwitch = 0; uplinkPort = 0; }

            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = existing > 0
                    ? """
                      UPDATE switches SET name = $n, ports = $p, subnet = $s, host_id = $h, uplink = $u, uplink_switch = $us,
                             uplink_port = $up, kind = $k, runs_on = $ro
                      WHERE id = $id
                      """
                    : """
                      INSERT INTO switches (name, ports, subnet, host_id, uplink, uplink_switch, uplink_port, kind, runs_on, auto, auto_key)
                      VALUES ($n, $p, $s, $h, $u, $us, $up, $k, $ro, $a, $key)
                      """;
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$p", ports);
                cmd.Parameters.AddWithValue("$s", subnet ?? "");
                cmd.Parameters.AddWithValue("$h", Math.Max(0, hostId));
                cmd.Parameters.AddWithValue("$u", uplink);
                cmd.Parameters.AddWithValue("$us", uplinkSwitch);
                cmd.Parameters.AddWithValue("$up", uplinkPort);
                cmd.Parameters.AddWithValue("$k", kind);
                cmd.Parameters.AddWithValue("$ro", runsOn);
                if (existing > 0) cmd.Parameters.AddWithValue("$id", existing);
                else { cmd.Parameters.AddWithValue("$a", source); cmd.Parameters.AddWithValue("$key", key); }
                cmd.ExecuteNonQuery();
            }
            long id2 = existing;
            if (existing == 0)
            {
                using var last = conn.CreateCommand();
                last.Transaction = tx;
                last.CommandText = "SELECT last_insert_rowid()";
                id2 = Convert.ToInt64(last.ExecuteScalar());
            }
            if (hostId > 0)
            {
                // A switch's own device is placed by its uplink, not as a device on a port.
                using var drop = conn.CreateCommand();
                drop.Transaction = tx;
                drop.CommandText = "DELETE FROM placements WHERE host_id = $h";
                drop.Parameters.AddWithValue("$h", hostId);
                drop.ExecuteNonQuery();
            }
            tx.Commit();
            return id2;
        }
    }

    /// <summary>
    /// Places devices on the switches a source reported, but never over a placement you made: a device that
    /// has one of its own is left where you put it. Returns how many placements were made or changed.
    /// </summary>
    public int ApplyAutoPlacements(string source, IReadOnlyList<(long HostId, long SwitchId, int Port)> wanted)
    {
        if (wanted.Count == 0) return 0;
        lock (_lock)
        {
            using var conn = Open();
            var switches = GetSwitchesInternal(conn).ToDictionary(s => s.Id);
            var owners = switches.Values.Where(s => s.HostId > 0).Select(s => s.HostId).ToHashSet();
            var current = new Dictionary<long, (long SwitchId, int Port, string Auto)>();
            using (var read = conn.CreateCommand())
            {
                read.CommandText = "SELECT host_id, switch_id, port, auto FROM placements";
                using var r = read.ExecuteReader();
                while (r.Read()) current[r.GetInt64(0)] = (r.GetInt64(1), r.GetInt32(2), r.GetString(3));
            }
            var changed = 0;
            using var tx = conn.BeginTransaction();
            foreach (var (hostId, switchId, rawPort) in wanted)
            {
                if (owners.Contains(hostId) || !switches.TryGetValue(switchId, out var sw)) continue;
                var port = HasNoPorts(sw.Kind) ? 0 : rawPort < 0 || rawPort > sw.Ports ? 0 : rawPort;
                if (current.TryGetValue(hostId, out var now))
                {
                    if (now.Auto == "") continue;                                  // yours: left alone
                    if (Rank(source) < Rank(now.Auto)) continue;                    // a better-informed source placed it
                    if (now.SwitchId == switchId && now.Port == port) continue;     // already there
                }
                using var put = conn.CreateCommand();
                put.Transaction = tx;
                put.CommandText = """
                    INSERT INTO placements (host_id, switch_id, port, auto) VALUES ($h, $s, $p, $a)
                    ON CONFLICT(host_id) DO UPDATE SET switch_id = $s, port = $p, auto = $a
                    """;
                put.Parameters.AddWithValue("$h", hostId);
                put.Parameters.AddWithValue("$s", switchId);
                put.Parameters.AddWithValue("$p", port);
                put.Parameters.AddWithValue("$a", source);
                put.ExecuteNonQuery();
                changed++;
            }
            tx.Commit();
            return changed;
        }
    }

    /// <summary>Which source knows more when two place the same device: the controller, which sees the Wi-Fi too, over a switch's bridge table.</summary>
    private static int Rank(string source) => source switch { "unifi" => 2, "snmp" => 1, _ => 0 };

    /// <summary>
    /// Takes back what a source drew. Its automatic placements go. Its automatic switches go too, unless
    /// something of yours depends on one (a device you placed on it, a switch you plugged into it): that
    /// one becomes yours and stays. With <paramref name="source"/> null, every source's. Returns
    /// (switches removed, switches kept as yours).
    /// </summary>
    public (int Removed, int Kept) ClearAutoMap(string? source = null)
    {
        var done = ReleaseOrDeleteAuto(source, null);
        // Starting again from nothing also forgets the drawn switches you deleted and asked not to see again.
        var ignored = GetAutoIgnored();
        if (source is null) ignored.Clear(); else ignored.RemoveWhere(k => k.StartsWith(source + "|", StringComparison.Ordinal));
        SetSetting("gearIgnored", JsonSerializer.Serialize(ignored.OrderBy(k => k)));
        return done;
    }

    /// <summary>The drawn switches, access points and SSIDs you deleted, as "source|key": deleting one means you don't want it, so it isn't drawn again.</summary>
    public HashSet<string> GetAutoIgnored()
    {
        try { return JsonSerializer.Deserialize<HashSet<string>>(GetSetting("gearIgnored") ?? "[]") ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>Remembers that a drawn switch is being deleted on purpose. Does nothing for one you recorded.</summary>
    public void IgnoreAutoSwitch(long id)
    {
        var a = GetAutoSwitches().FirstOrDefault(x => x.Id == id);
        if (a is null) return;
        var ignored = GetAutoIgnored();
        ignored.Add(a.Source + "|" + a.Key);
        SetSetting("gearIgnored", JsonSerializer.Serialize(ignored.OrderBy(k => k)));
    }

    /// <summary>The same for the automatic switches of a source that its last read no longer lists.</summary>
    public (int Removed, int Kept) RemoveStaleAutoSwitches(string source, IReadOnlySet<string> currentKeys) => ReleaseOrDeleteAuto(source, currentKeys);

    private (int Removed, int Kept) ReleaseOrDeleteAuto(string? source, IReadOnlySet<string>? keep)
    {
        List<(long Id, string Source, string Key, string Uplink, long UplinkSwitch, long RunsOn, bool Auto)> all = new();
        HashSet<long> withMine = new();
        lock (_lock)
        {
            using var conn = Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, auto, auto_key, uplink, uplink_switch, runs_on FROM switches";
                using var r = cmd.ExecuteReader();
                while (r.Read()) all.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetString(1) != ""));
            }
            // The placements that are the user's own: the ones on a switch that make it theirs.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT switch_id FROM placements WHERE auto = ''";
                using var r = cmd.ExecuteReader();
                while (r.Read()) withMine.Add(r.GetInt64(0));
            }
            // Its automatic placements go in every case: with no source given, or for a stale switch, along with it.
            if (keep is null)
            {
                using var del = conn.CreateCommand();
                del.CommandText = source is null ? "DELETE FROM placements WHERE auto <> ''" : "DELETE FROM placements WHERE auto = $s";
                if (source is not null) del.Parameters.AddWithValue("$s", source);
                del.ExecuteNonQuery();
            }
        }

        var targets = all.Where(s => s.Auto && (source is null || s.Source == source) && (keep is null || !keep.Contains(s.Key)))
            .Select(s => s.Id).ToHashSet();
        if (targets.Count == 0) return (0, 0);

        // Children before parents, so what hangs off a switch is settled before the switch is.
        var order = new List<long>();
        var seen = new HashSet<long>();
        void Visit(long id)
        {
            if (!seen.Add(id)) return;
            foreach (var child in all.Where(c => (c.Uplink == "switch" && c.UplinkSwitch == id) || c.RunsOn == id)) Visit(child.Id);
            if (targets.Contains(id)) order.Add(id);
        }
        foreach (var id in targets) Visit(id);

        var gone = new HashSet<long>();
        int removed = 0, kept = 0;
        foreach (var id in order)
        {
            var depends = withMine.Contains(id) || all.Any(c => !gone.Contains(c.Id) && c.Id != id && ((c.Uplink == "switch" && c.UplinkSwitch == id) || c.RunsOn == id));
            if (depends)
            {
                lock (_lock)
                {
                    using var conn = Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE switches SET auto = '', auto_key = '' WHERE id = $id";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
                kept++;
            }
            else
            {
                DeleteSwitch(id);
                DeleteSnmpConfig(id);
                gone.Add(id);
                removed++;
            }
        }
        return (removed, kept);
    }

    // ------------------------------------------------------------ Wi-Fi

    public Dictionary<long, WifiLink> GetWifi()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT host_id, ap_mac, ap_name, ssid, band, channel, signal, noise, tx_mbps, rx_mbps, satisfaction, roams,
                       since, updated_at, moved_from, moved_at FROM wifi_links
                """;
            var map = new Dictionary<long, WifiLink>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int? N(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
                map[r.GetInt64(0)] = new WifiLink(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5),
                    N(6), N(7), N(8), N(9), N(10), r.GetInt32(11), r.GetString(12), r.GetString(13), r.GetString(14), r.GetString(15));
            }
            return map;
        }
    }

    /// <summary>
    /// Records what each Wi-Fi device last reported, and notes when one changes access point. Devices now
    /// on a cable (<paramref name="wired"/>) have no Wi-Fi reading any more, so theirs goes. A device that
    /// just isn't in this read keeps its last reading, so a quiet minute doesn't blank it.
    /// </summary>
    public void SaveWifi(IReadOnlyList<WifiLink> links, IReadOnlyCollection<long> wired)
    {
        lock (_lock)
        {
            using var conn = Open();
            var before = new Dictionary<long, (string ApMac, string ApName, string Since, string MovedFrom, string MovedAt)>();
            using (var read = conn.CreateCommand())
            {
                read.CommandText = "SELECT host_id, ap_mac, ap_name, since, moved_from, moved_at FROM wifi_links";
                using var r = read.ExecuteReader();
                while (r.Read()) before[r.GetInt64(0)] = (r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5));
            }
            using var tx = conn.BeginTransaction();
            foreach (var l in links)
            {
                var prev = before.GetValueOrDefault(l.HostId);
                var moved = prev.ApMac != null && prev.ApMac != "" && l.ApMac != "" && !string.Equals(prev.ApMac, l.ApMac, StringComparison.OrdinalIgnoreCase);
                using var put = conn.CreateCommand();
                put.Transaction = tx;
                put.CommandText = """
                    INSERT INTO wifi_links (host_id, ap_mac, ap_name, ssid, band, channel, signal, noise, tx_mbps, rx_mbps, satisfaction, roams,
                                            since, updated_at, moved_from, moved_at)
                    VALUES ($h, $am, $an, $ss, $b, $c, $sg, $nz, $tx, $rx, $st, $ro, $since, $now, $mf, $mat)
                    ON CONFLICT(host_id) DO UPDATE SET ap_mac = $am, ap_name = $an, ssid = $ss, band = $b, channel = $c, signal = $sg, noise = $nz,
                        tx_mbps = $tx, rx_mbps = $rx, satisfaction = $st, roams = $ro, since = $since, updated_at = $now, moved_from = $mf, moved_at = $mat
                    """;
                put.Parameters.AddWithValue("$h", l.HostId);
                put.Parameters.AddWithValue("$am", l.ApMac);
                put.Parameters.AddWithValue("$an", l.ApName);
                put.Parameters.AddWithValue("$ss", l.Ssid);
                put.Parameters.AddWithValue("$b", l.Band);
                put.Parameters.AddWithValue("$c", l.Channel);
                put.Parameters.AddWithValue("$sg", (object?)l.Signal ?? DBNull.Value);
                put.Parameters.AddWithValue("$nz", (object?)l.Noise ?? DBNull.Value);
                put.Parameters.AddWithValue("$tx", (object?)l.TxMbps ?? DBNull.Value);
                put.Parameters.AddWithValue("$rx", (object?)l.RxMbps ?? DBNull.Value);
                put.Parameters.AddWithValue("$st", (object?)l.Satisfaction ?? DBNull.Value);
                put.Parameters.AddWithValue("$ro", l.Roams);
                put.Parameters.AddWithValue("$since", prev.Since is { Length: > 0 } && !moved ? prev.Since : l.UpdatedAt);
                put.Parameters.AddWithValue("$now", l.UpdatedAt);
                put.Parameters.AddWithValue("$mf", moved ? prev.ApName : prev.MovedFrom ?? "");
                put.Parameters.AddWithValue("$mat", moved ? l.UpdatedAt : prev.MovedAt ?? "");
                put.ExecuteNonQuery();
            }
            foreach (var id in wired)
            {
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM wifi_links WHERE host_id = $h";
                del.Parameters.AddWithValue("$h", id);
                del.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void ClearWifi()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM wifi_links";
            cmd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------ gear health

    public List<GearRow> GetGear()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT mac, source, name, kind, model, version, upgrade_to, uptime, online, ip, ports, cpu, mem, temp, poe, poe_budget,
                       ports_json, ssids_json, reboots, last_reboot, first_seen, updated_at
                FROM gear_health ORDER BY name COLLATE NOCASE, mac
                """;
            var list = new List<GearRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
                List<T> Json<T>(int i)
                {
                    try { return JsonSerializer.Deserialize<List<T>>(r.GetString(i)) ?? new(); }
                    catch (JsonException) { return new(); }
                }
                list.Add(new GearRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                    r.GetInt64(7), r.GetInt32(8) != 0, r.GetString(9), r.GetInt32(10), D(11), D(12), D(13), D(14), D(15),
                    Json<GearPort>(16), Json<GearSsid>(17), r.GetInt32(18), r.GetString(19), r.GetString(20), r.GetString(21)));
            }
            return list;
        }
    }

    /// <summary>
    /// Records the gear's state. An uptime that went down since the last read means it restarted, which is counted
    /// and dated. Gear a source no longer lists is dropped, for the sources that were read this time.
    /// </summary>
    public void SaveGear(IReadOnlyList<GearDevice> devices, IReadOnlySet<string> sourcesRead, string now)
    {
        lock (_lock)
        {
            using var conn = Open();
            var before = new Dictionary<string, (long Uptime, int Reboots, string LastReboot, string FirstSeen)>(StringComparer.OrdinalIgnoreCase);
            using (var read = conn.CreateCommand())
            {
                read.CommandText = "SELECT mac, uptime, reboots, last_reboot, first_seen, source FROM gear_health";
                using var r = read.ExecuteReader();
                while (r.Read()) before[r.GetString(0)] = (r.GetInt64(1), r.GetInt32(2), r.GetString(3), r.GetString(4));
            }
            using var tx = conn.BeginTransaction();
            foreach (var d in devices)
            {
                var prev = before.TryGetValue(d.Mac, out var p) ? p : (Uptime: 0L, Reboots: 0, LastReboot: "", FirstSeen: now);
                // 60 seconds of slack: two sources, or two reads, can disagree by a little without it being a restart.
                var rebooted = before.ContainsKey(d.Mac) && d.UptimeSeconds > 0 && d.UptimeSeconds + 60 < prev.Uptime;
                using var put = conn.CreateCommand();
                put.Transaction = tx;
                put.CommandText = """
                    INSERT INTO gear_health (mac, source, name, kind, model, version, upgrade_to, uptime, online, ip, ports, cpu, mem, temp, poe, poe_budget,
                                             ports_json, ssids_json, reboots, last_reboot, first_seen, updated_at)
                    VALUES ($mac, $src, $n, $k, $mo, $v, $up, $ut, $on, $ip, $pc, $cpu, $mem, $t, $poe, $pb, $pj, $sj, $rb, $lr, $fs, $now)
                    ON CONFLICT(mac) DO UPDATE SET source = $src, name = $n, kind = $k, model = $mo, version = $v, upgrade_to = $up, uptime = $ut,
                        online = $on, ip = $ip, ports = $pc, cpu = $cpu, mem = $mem, temp = $t, poe = $poe, poe_budget = $pb, ports_json = $pj,
                        ssids_json = $sj, reboots = $rb, last_reboot = $lr, updated_at = $now
                    """;
                put.Parameters.AddWithValue("$mac", d.Mac);
                put.Parameters.AddWithValue("$src", d.Source);
                put.Parameters.AddWithValue("$n", d.Name);
                put.Parameters.AddWithValue("$k", d.Kind);
                put.Parameters.AddWithValue("$mo", d.Model);
                put.Parameters.AddWithValue("$v", d.Version);
                put.Parameters.AddWithValue("$up", d.UpgradeTo);
                put.Parameters.AddWithValue("$ut", d.UptimeSeconds);
                put.Parameters.AddWithValue("$on", d.Online ? 1 : 0);
                put.Parameters.AddWithValue("$ip", d.Ip);
                put.Parameters.AddWithValue("$pc", d.PortCount);
                put.Parameters.AddWithValue("$cpu", (object?)d.Cpu ?? DBNull.Value);
                put.Parameters.AddWithValue("$mem", (object?)d.Mem ?? DBNull.Value);
                put.Parameters.AddWithValue("$t", (object?)d.TempC ?? DBNull.Value);
                put.Parameters.AddWithValue("$poe", (object?)d.PoeWatts ?? DBNull.Value);
                put.Parameters.AddWithValue("$pb", (object?)d.PoeBudget ?? DBNull.Value);
                put.Parameters.AddWithValue("$pj", JsonSerializer.Serialize(d.Ports));
                put.Parameters.AddWithValue("$sj", JsonSerializer.Serialize(d.Ssids));
                put.Parameters.AddWithValue("$rb", prev.Reboots + (rebooted ? 1 : 0));
                put.Parameters.AddWithValue("$lr", rebooted ? now : prev.LastReboot);
                put.Parameters.AddWithValue("$fs", prev.FirstSeen);
                put.Parameters.AddWithValue("$now", now);
                put.ExecuteNonQuery();
            }
            // Gear a source read this time but no longer lists has gone from the network.
            var listed = devices.Select(d => d.Mac).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (mac, _) in before)
            {
                if (listed.Contains(mac)) continue;
                using var src = conn.CreateCommand();
                src.Transaction = tx;
                src.CommandText = "SELECT source FROM gear_health WHERE mac = $m";
                src.Parameters.AddWithValue("$m", mac);
                if (src.ExecuteScalar() is string s && sourcesRead.Contains(s))
                {
                    using var del = conn.CreateCommand();
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM gear_health WHERE mac = $m";
                    del.Parameters.AddWithValue("$m", mac);
                    del.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    /// <summary>Forgets the gear's recorded state: all of it, or only what one source gave.</summary>
    public void ClearGear(string? source = null)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = source is null ? "DELETE FROM gear_health" : "DELETE FROM gear_health WHERE source = $s";
            if (source is not null) cmd.Parameters.AddWithValue("$s", source);
            cmd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------ networks read from the gateway

    public const int MaxGatewayNetworks = 16;

    /// <summary>The networks you've marked "read from the gateway", whether or not the feature is on.</summary>
    public List<string> GearVlanNetworkList()
    {
        try { return JsonSerializer.Deserialize<List<string>>(GetSetting("gearVlanNets") ?? "[]") ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>The networks being read from the gateway right now: none unless the feature is on.</summary>
    public List<string> GearVlanNetworks() => GearVlansOn ? GearVlanNetworkList() : new();

    public void SetGearVlanNetworks(IEnumerable<string> networks) => SetSetting("gearVlanNets", JsonSerializer.Serialize(networks.Distinct().ToList()));

    /// <summary>
    /// Hides the devices on networks that are no longer read from the gateway: forgotten, not deleted, so a name or note you gave
    /// one is still there when it comes back. Returns how many.
    /// </summary>
    public int ForgetGatewayHosts(IReadOnlyCollection<string> networks)
    {
        if (networks.Count == 0) return 0;
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            var names = networks.Select((n, i) => ($"$n{i}", n)).ToList();
            cmd.CommandText = $"UPDATE hosts SET forgotten = 1, watched = 0, online = 0 WHERE forgotten = 0 AND subnet IN ({string.Join(", ", names.Select(n => n.Item1))})";
            foreach (var (name, value) in names) cmd.Parameters.AddWithValue(name, value);
            return cmd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------ gateway events

    public sealed record GearEventLogItem(string At, string Kind, string Title);

    /// <summary>The last few things a gateway reported that BAMF told you about, newest first.</summary>
    public List<GearEventLogItem> GetGearEventLog()
    {
        try { return JsonSerializer.Deserialize<List<GearEventLogItem>>(GetSetting("gearEventLog") ?? "[]") ?? new(); }
        catch (JsonException) { return new(); }
    }

    public void LogGearEvent(string kind, string title, string at)
    {
        var log = GetGearEventLog();
        log.Insert(0, new GearEventLogItem(at, kind, title));
        SetSetting("gearEventLog", JsonSerializer.Serialize(log.Take(20).ToList()));
    }
}
