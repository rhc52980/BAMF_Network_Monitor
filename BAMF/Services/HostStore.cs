using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

public record HostRecord(
    long Id, string Mac, string Ip, string Hostname, string CustomName, string Vendor, string Subnet,
    bool Online, bool Known, bool Ignored, bool Watched, bool Forgotten, string Note, string FirstSeen, string LastSeen,
    string OsGuess, string Link, string MdnsName, string MdnsServices);

/// <summary>SQLite-backed store for discovered hosts.</summary>
public class HostStore
{
    private readonly string _connString;
    private readonly object _lock = new();
    private readonly int _retentionDays;
    private readonly List<long> _recoveredThisCycle = new();
    private readonly object _recoveredLock = new();

    /// <summary>Returns and clears the watched hosts that recovered since the last drain.</summary>
    public List<HostRecord> DrainRecovered()
    {
        lock (_recoveredLock)
        {
            if (_recoveredThisCycle.Count == 0) return new();
            using var conn = Open();
            var list = _recoveredThisCycle
                .Distinct()
                .Select(id => GetByIdInternal(conn, id))
                .Where(h => h is not null)
                .ToList()!;
            _recoveredThisCycle.Clear();
            return list!;
        }
    }

    public HostStore(IConfiguration config)
    {
        _retentionDays = Math.Max(1, config.GetValue("Bamf:HistoryRetentionDays", 90));
        var dbPath = config["Bamf:DatabasePath"] ?? "bamf.db";
        if (!Path.IsPathRooted(dbPath))
            dbPath = Path.Combine(AppContext.BaseDirectory, dbPath);

        _connString = $"Data Source={dbPath}";
        Init();
    }

    private void Init()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS hosts (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                mac         TEXT NOT NULL UNIQUE,
                ip          TEXT NOT NULL,
                hostname    TEXT NOT NULL DEFAULT '',
                custom_name TEXT NOT NULL DEFAULT '',
                vendor      TEXT NOT NULL DEFAULT '',
                subnet      TEXT NOT NULL DEFAULT '',
                online      INTEGER NOT NULL DEFAULT 0,
                known       INTEGER NOT NULL DEFAULT 0,
                ignored     INTEGER NOT NULL DEFAULT 0,
                watched     INTEGER NOT NULL DEFAULT 0,
                forgotten   INTEGER NOT NULL DEFAULT 0,
                note        TEXT NOT NULL DEFAULT '',
                first_seen  TEXT NOT NULL,
                last_seen   TEXT NOT NULL,
                os_guess    TEXT NOT NULL DEFAULT '',
                link        TEXT NOT NULL DEFAULT ''
            );
            """;
        cmd.ExecuteNonQuery();

        using (var ev = conn.CreateCommand())
        {
            ev.CommandText = """
                CREATE TABLE IF NOT EXISTS events (
                    id      INTEGER PRIMARY KEY AUTOINCREMENT,
                    host_id INTEGER NOT NULL,
                    type    TEXT NOT NULL,          -- 'online' | 'offline'
                    at      TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_events_host ON events(host_id, at DESC);
                """;
            ev.ExecuteNonQuery();
        }

        PruneEventsInternal(conn, _retentionDays);

        // Migrations: add columns missing from databases created by older versions.
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(hosts)";
            using var r = check.ExecuteReader();
            while (r.Read()) existing.Add(r.GetString(1));
        }
        foreach (var (col, ddl) in new[]
        {
            ("subnet", "ALTER TABLE hosts ADD COLUMN subnet TEXT NOT NULL DEFAULT ''"),
            ("custom_name", "ALTER TABLE hosts ADD COLUMN custom_name TEXT NOT NULL DEFAULT ''"),
            ("ignored", "ALTER TABLE hosts ADD COLUMN ignored INTEGER NOT NULL DEFAULT 0"),
            ("watched", "ALTER TABLE hosts ADD COLUMN watched INTEGER NOT NULL DEFAULT 0"),
            ("forgotten", "ALTER TABLE hosts ADD COLUMN forgotten INTEGER NOT NULL DEFAULT 0"),
            ("note", "ALTER TABLE hosts ADD COLUMN note TEXT NOT NULL DEFAULT ''"),
            ("os_guess", "ALTER TABLE hosts ADD COLUMN os_guess TEXT NOT NULL DEFAULT ''"),
            ("link", "ALTER TABLE hosts ADD COLUMN link TEXT NOT NULL DEFAULT ''"),
            // Consecutive scans of its network that have not seen this host.
            ("misses", "ALTER TABLE hosts ADD COLUMN misses INTEGER NOT NULL DEFAULT 0"),
            // Learned passively from the device's own mDNS announcements. Kept apart
            // from hostname so a DNS or NetBIOS name is never overwritten by one.
            ("mdns_name", "ALTER TABLE hosts ADD COLUMN mdns_name TEXT NOT NULL DEFAULT ''"),
            ("mdns_services", "ALTER TABLE hosts ADD COLUMN mdns_services TEXT NOT NULL DEFAULT ''"),
        })
        {
            if (existing.Contains(col)) continue;
            using var alter = conn.CreateCommand();
            alter.CommandText = ddl;
            alter.ExecuteNonQuery();
        }

        using (var settings = conn.CreateCommand())
        {
            settings.CommandText = """
                CREATE TABLE IF NOT EXISTS settings (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """;
            settings.ExecuteNonQuery();
        }

        // One row per address change, so "when did this device move, and from
        // where" is answerable. Devices that predate the table get a single seed
        // row - their current address, dated from first_seen - which is the most
        // that can honestly be said about them; earlier changes were not kept.
        using (var ih = conn.CreateCommand())
        {
            ih.CommandText = """
                CREATE TABLE IF NOT EXISTS ip_history (
                    id      INTEGER PRIMARY KEY AUTOINCREMENT,
                    host_id INTEGER NOT NULL,
                    ip      TEXT NOT NULL,
                    at      TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_iphist_host ON ip_history(host_id, at);
                INSERT INTO ip_history (host_id, ip, at)
                    SELECT id, ip, first_seen FROM hosts
                    WHERE ip <> '' AND id NOT IN (SELECT host_id FROM ip_history);
                """;
            ih.ExecuteNonQuery();
        }

        ScrubSyntheticHostnames(conn);
    }

    /// <summary>Every address a host has been seen at, oldest first, with when each began.</summary>
    public List<(string Ip, string At)> GetIpHistory(long hostId)
    {
        lock (_lock)
        {
            var list = new List<(string, string)>();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ip, at FROM ip_history WHERE host_id = $id ORDER BY at ASC, id ASC";
            cmd.Parameters.AddWithValue("$id", hostId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
            return list;
        }
    }

    private static void AddIpChange(SqliteConnection conn, long hostId, string ip, string at)
    {
        // The history is what was observed, so it must never say a device moved
        // to the address it was already at. Normally the stored address and the
        // latest row agree, but a hand-edited row or a database restored from a
        // backup can put them out of step; the observation wins, not the column.
        using (var last = conn.CreateCommand())
        {
            last.CommandText = "SELECT ip FROM ip_history WHERE host_id = $h ORDER BY id DESC LIMIT 1";
            last.Parameters.AddWithValue("$h", hostId);
            if (last.ExecuteScalar() is string prev && prev == ip) return;
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO ip_history (host_id, ip, at) VALUES ($h, $ip, $at)";
        cmd.Parameters.AddWithValue("$h", hostId);
        cmd.Parameters.AddWithValue("$ip", ip);
        cmd.Parameters.AddWithValue("$at", at);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Blanks stored hostnames that are really a device's MAC in disguise.
    /// The resolver has refused such names for a while, but only on the
    /// NetBIOS path, and UpsertSeen deliberately keeps an existing hostname
    /// when a fresh lookup returns nothing - so a junk name that got in once
    /// stayed forever, showing under the device's real name in the dashboard.
    /// Runs at startup, so an existing database is cleaned on the next update
    /// rather than only protecting devices seen from now on. A real name that
    /// resolves later still replaces the blank as it always has.
    /// </summary>
    private static void ScrubSyntheticHostnames(SqliteConnection conn)
    {
        var junk = new List<(long Id, string Name)>();
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT id, hostname FROM hosts WHERE hostname <> ''";
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(1);
                if (NetBiosResolver.LooksMacDerived(name)) junk.Add((r.GetInt64(0), name));
            }
        }
        foreach (var (id, _) in junk)
        {
            using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE hosts SET hostname = '' WHERE id = $id";
            upd.Parameters.AddWithValue("$id", id);
            upd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Removes a saved setting so the appsettings.json value becomes authoritative
    /// again. Absent rather than blank: an empty string is a legitimate value for
    /// some keys, so "unset" has to be the row not existing.
    /// </summary>
    public void DeleteSetting(string key)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM settings WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// How long history is kept, a value saved from the dashboard winning over
    /// appsettings.json. Read at prune time rather than captured at startup, so
    /// changing it does not need a restart.
    /// </summary>
    public int RetentionDays
    {
        get
        {
            var db = GetSetting("historyRetentionDays");
            if (db is not null && int.TryParse(db, out var days)) return Math.Max(1, days);
            return _retentionDays;
        }
    }

    /// <summary>Deletes events older than the configured retention window.</summary>
    public void PruneEvents()
    {
        var days = RetentionDays;
        lock (_lock)
        {
            using var conn = Open();
            PruneEventsInternal(conn, days);
        }
    }

    // Takes the window as an argument because Init() prunes before the settings
    // table exists, so it can only use the value from appsettings.json.
    private void PruneEventsInternal(SqliteConnection conn, int retentionDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("o");
        using (var prune = conn.CreateCommand())
        {
            prune.CommandText = "DELETE FROM events WHERE at < $cutoff";
            prune.Parameters.AddWithValue("$cutoff", cutoff);
            prune.ExecuteNonQuery();
        }
        // Address history ages out on the same window, except each host's most
        // recent row, so the current address always has a known start.
        using (var prune = conn.CreateCommand())
        {
            prune.CommandText = """
                DELETE FROM ip_history WHERE at < $cutoff
                  AND id NOT IN (SELECT MAX(id) FROM ip_history GROUP BY host_id)
                """;
            prune.Parameters.AddWithValue("$cutoff", cutoff);
            try { prune.ExecuteNonQuery(); } catch (SqliteException) { /* table not created yet on first Init */ }
        }
    }

    /// <summary>Recent events across all hosts (excluding ignored ones), newest first.</summary>
    public List<(string Type, string At, long HostId, string Mac, string Ip, string Hostname, string CustomName, string Subnet)>
        GetRecentEvents(int limit = 200)
    {
        lock (_lock)
        {
            var list = new List<(string, string, long, string, string, string, string, string)>();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT e.type, e.at, h.id, h.mac, h.ip, h.hostname, h.custom_name, h.subnet
                FROM events e JOIN hosts h ON h.id = e.host_id
                WHERE h.ignored = 0 AND h.forgotten = 0
                ORDER BY e.at DESC LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3),
                          r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7)));
            return list;
        }
    }

    public string? GetSetting(string key)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO settings (key, value) VALUES ($k, $v)
                ON CONFLICT(key) DO UPDATE SET value = $v
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Upserts a scan result. Returns whether this MAC is brand new and whether it's ignored.
    /// If <paramref name="autoIgnore"/> is true and the host is new, it's created pre-ignored.
    /// Ignored hosts are still updated (ip/last_seen) but generate no events.
    /// </summary>
    public (bool IsNew, bool Ignored) UpsertSeen(string mac, string ip, string hostname, string vendor, string subnet, bool autoIgnore = false)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow.ToString("o");
            using var conn = Open();

            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT id, hostname, online, ignored, watched, ip FROM hosts WHERE mac = $mac";
                check.Parameters.AddWithValue("$mac", mac);
                using var r = check.ExecuteReader();
                if (r.Read())
                {
                    var hostId = r.GetInt64(0);
                    var existingHostname = r.GetString(1);
                    var wasOnline = r.GetInt64(2) == 1;
                    var isIgnored = r.GetInt64(3) == 1;
                    var isWatched = r.GetInt64(4) == 1;
                    var oldIp = r.GetString(5);
                    // Keep an existing hostname if reverse DNS failed this time.
                    // Keep an existing hostname if reverse DNS/NetBIOS failed this
                    // time — UNLESS the stored name is MAC-derived junk (e.g. a
                    // printer's "C22E4F700000"), which we want to let go of.
                    string newHostname;
                    if (!string.IsNullOrEmpty(hostname))
                        newHostname = hostname;
                    else if (LooksMacJunk(existingHostname))
                        newHostname = "";
                    else
                        newHostname = existingHostname;

                    using var upd = conn.CreateCommand();
                    upd.CommandText = """
                        UPDATE hosts
                        SET ip = $ip, hostname = $hostname, vendor = $vendor,
                            subnet = $subnet, online = 1, forgotten = 0, last_seen = $now, misses = 0
                        WHERE mac = $mac
                        """;
                    upd.Parameters.AddWithValue("$ip", ip);
                    upd.Parameters.AddWithValue("$hostname", newHostname);
                    upd.Parameters.AddWithValue("$vendor", vendor);
                    upd.Parameters.AddWithValue("$subnet", subnet);
                    upd.Parameters.AddWithValue("$now", now);
                    upd.Parameters.AddWithValue("$mac", mac);
                    upd.ExecuteNonQuery();

                    // A different address for a known MAC is the whole point of
                    // the history table; the same address is not worth a row.
                    if (!string.Equals(oldIp, ip, StringComparison.Ordinal))
                        AddIpChange(conn, hostId, ip, now);

                    if (!wasOnline && !isIgnored)
                    {
                        AddEvent(conn, hostId, "online", now);
                        if (isWatched)
                            lock (_recoveredLock) _recoveredThisCycle.Add(hostId);
                    }
                    return (false, isIgnored);
                }
            }

            using var ins = conn.CreateCommand();
            ins.CommandText = """
                INSERT INTO hosts (mac, ip, hostname, vendor, subnet, online, known, ignored, first_seen, last_seen)
                VALUES ($mac, $ip, $hostname, $vendor, $subnet, 1, 0, $ignored, $now, $now)
                """;
            ins.Parameters.AddWithValue("$mac", mac);
            ins.Parameters.AddWithValue("$ip", ip);
            ins.Parameters.AddWithValue("$hostname", hostname);
            ins.Parameters.AddWithValue("$vendor", vendor);
            ins.Parameters.AddWithValue("$subnet", subnet);
            ins.Parameters.AddWithValue("$ignored", autoIgnore ? 1 : 0);
            ins.Parameters.AddWithValue("$now", now);
            ins.ExecuteNonQuery();

            long newId;
            using (var lastId = conn.CreateCommand())
            {
                lastId.CommandText = "SELECT last_insert_rowid()";
                newId = (long)lastId.ExecuteScalar()!;
            }
            // The first address starts the history for every new device, ignored
            // ones included: a randomised-MAC phone that someone later un-ignores
            // should not have a hole where its history began.
            AddIpChange(conn, newId, ip, now);
            if (!autoIgnore) AddEvent(conn, newId, "online", now);
            return (true, autoIgnore);
        }
    }

    /// <summary>
    /// True for MAC-derived pseudo-hostnames like "C22E4F700000" that some
    /// printers/IoT devices report — mostly hex, few or no wordlike letters.
    /// </summary>
    private static bool LooksMacJunk(string name)
    {
        var t = (name ?? "").Trim();
        if (t.Length < 8) return false;
        int hex = 0, non = 0;
        foreach (var c in t)
        {
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (isHex) hex++;
            else if (c != '-' && c != '_') non++;
        }
        var total = hex + non;
        return total > 0 && non <= 1 && (double)hex / total >= 0.8;
    }

    private static void AddEvent(SqliteConnection conn, long hostId, string type, string at)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO events (host_id, type, at) VALUES ($h, $t, $a)";
        cmd.Parameters.AddWithValue("$h", hostId);
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$a", at);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Marks every host NOT in <paramref name="seenMacs"/> as offline.
    /// Returns the watched hosts that just transitioned offline this cycle.
    /// </summary>
    /// <param name="coveredSubnets">
    /// Networks this scan pass actually visited. Absent (or null) means the pass
    /// covered everything, so any online host missing from <paramref name="seenMacs"/>
    /// is genuinely down - the original behaviour. When subnets run on different
    /// intervals a pass covers only some of them, and the hosts on the rest were
    /// never looked for: judging them by this pass's MACs would declare an entire
    /// network offline every time a faster one ticked. Scoping the query to the
    /// networks actually scanned is what makes per-subnet intervals safe.
    /// </param>
    /// <param name="missThreshold">
    /// How many consecutive scans of its network must fail to see a host before
    /// it is declared offline. 1 is the old behaviour: gone from one scan, gone.
    /// Counted in scans rather than minutes so it means the same thing on a
    /// network scanned every 15 seconds and one scanned every 10 minutes, and
    /// a paused network - never covered - never accumulates misses at all.
    /// The counter lives in the row, so a service restart neither forgives a
    /// miss nor invents one.
    /// </param>
    public List<HostRecord> MarkOffline(IReadOnlySet<string> seenMacs,
        IReadOnlyCollection<string>? coveredSubnets = null, int missThreshold = 1)
    {
        if (missThreshold < 1) missThreshold = 1;
        lock (_lock)
        {
            var now = DateTime.UtcNow.ToString("o");
            var wentDown = new List<HostRecord>();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            if (coveredSubnets is null)
            {
                cmd.CommandText = "SELECT id, mac, ignored, watched, misses FROM hosts WHERE online = 1";
            }
            else if (coveredSubnets.Count == 0)
            {
                // Nothing was scanned, so nothing can be judged offline.
                return wentDown;
            }
            else
            {
                var names = coveredSubnets.Select((_, i) => $"$s{i}").ToList();
                cmd.CommandText =
                    $"SELECT id, mac, ignored, watched, misses FROM hosts WHERE online = 1 " +
                    $"AND subnet IN ({string.Join(", ", names)})";
                var i = 0;
                foreach (var s in coveredSubnets) cmd.Parameters.AddWithValue($"$s{i++}", s);
            }
            var toMark = new List<(long Id, string Mac, bool Ignored, bool Watched)>();
            var toCount = new List<(long Id, long Misses)>();
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var id = r.GetInt64(0);
                    var mac = r.GetString(1);
                    var ign = r.GetInt64(2) == 1;
                    var wat = r.GetInt64(3) == 1;
                    var misses = r.GetInt64(4);
                    if (seenMacs.Contains(mac)) continue;
                    // One more miss. Only when that reaches the threshold does the
                    // host actually go offline; until then just remember the count.
                    if (misses + 1 >= missThreshold) toMark.Add((id, mac, ign, wat));
                    else toCount.Add((id, misses + 1));
                }

            foreach (var (id, misses) in toCount)
            {
                using var bump = conn.CreateCommand();
                bump.CommandText = "UPDATE hosts SET misses = $m WHERE id = $id";
                bump.Parameters.AddWithValue("$m", misses);
                bump.Parameters.AddWithValue("$id", id);
                bump.ExecuteNonQuery();
            }

            foreach (var (id, mac, ign, wat) in toMark)
            {
                using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE hosts SET online = 0, misses = 0 WHERE mac = $mac";
                upd.Parameters.AddWithValue("$mac", mac);
                upd.ExecuteNonQuery();
                if (!ign) AddEvent(conn, id, "offline", now);
                if (wat && !ign)
                {
                    // Null would mean the row vanished between the two queries on this
                    // same connection; skip it rather than filter it out downstream.
                    var rec = GetByIdInternal(conn, id);
                    if (rec is not null) wentDown.Add(rec);
                }
            }
            return wentDown;
        }
    }

    private HostRecord? GetByIdInternal(SqliteConnection conn, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, mac, ip, hostname, custom_name, vendor, subnet, online, known, ignored, watched, forgotten, note, first_seen, last_seen, os_guess, link, mdns_name, mdns_services
            FROM hosts WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new HostRecord(
            r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.GetString(4), r.GetString(5), r.GetString(6),
            r.GetInt64(7) == 1, r.GetInt64(8) == 1, r.GetInt64(9) == 1, r.GetInt64(10) == 1,
            r.GetInt64(11) == 1, r.GetString(12), r.GetString(13), r.GetString(14),
            r.GetString(15), r.GetString(16), r.GetString(17), r.GetString(18));
    }

    /// <summary>UTC timestamp of the host's most recent "offline" event, if any.</summary>
    public DateTime? LastOfflineAt(long hostId)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT at FROM events WHERE host_id = $h AND type = 'offline' ORDER BY at DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$h", hostId);
            var v = cmd.ExecuteScalar() as string;
            return v is not null && DateTime.TryParse(v, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt : null;
        }
    }

    public bool SetIgnored(long id, bool ignored)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE hosts SET ignored = $ign WHERE id = $id";
            cmd.Parameters.AddWithValue("$ign", ignored ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool SetWatched(long id, bool watched)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE hosts SET watched = $w WHERE id = $id";
            cmd.Parameters.AddWithValue("$w", watched ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public List<(string Type, string At)> GetEvents(long hostId, int limit = 60)
    {
        lock (_lock)
        {
            var list = new List<(string, string)>();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT type, at FROM events WHERE host_id = $h ORDER BY at DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$h", hostId);
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
            return list;
        }
    }

    public List<HostRecord> GetAll()
    {
        lock (_lock)
        {
            var list = new List<HostRecord>();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, mac, ip, hostname, custom_name, vendor, subnet, online, known, ignored, watched, forgotten, note, first_seen, last_seen, os_guess, link, mdns_name, mdns_services
                FROM hosts ORDER BY subnet, ip
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new HostRecord(
                    r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.GetString(4), r.GetString(5), r.GetString(6),
                    r.GetInt64(7) == 1, r.GetInt64(8) == 1, r.GetInt64(9) == 1, r.GetInt64(10) == 1,
                    r.GetInt64(11) == 1, r.GetString(12), r.GetString(13), r.GetString(14),
                    r.GetString(15), r.GetString(16), r.GetString(17), r.GetString(18)));
            }
            return list;
        }
    }

    public bool SetName(long id, string name)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE hosts SET custom_name = $name WHERE id = $id";
            cmd.Parameters.AddWithValue("$name", name.Trim());
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool SetNote(long id, string note)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE hosts SET note = $note WHERE id = $id";
            cmd.Parameters.AddWithValue("$note", (note ?? "").Trim());
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Stores a device/OS guess. Empty string clears it.</summary>
    public bool SetOsGuess(long id, string guess)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE hosts SET os_guess = $g WHERE id = $id";
            cmd.Parameters.AddWithValue("$g", (guess ?? "").Trim());
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Stores the per-host link override. Empty string clears it.</summary>
    public bool SetLink(long id, string link)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE hosts SET link = $l WHERE id = $id";
            cmd.Parameters.AddWithValue("$l", (link ?? "").Trim());
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Fills in a passive guess for hosts that don't have one yet. Vendor and
    /// hostname only — no packets, so this is safe to run every scan.
    /// </summary>
    public void ApplyPassiveFingerprints()
    {
        lock (_lock)
        {
            using var conn = Open();
            var pending = new List<(long Id, string Vendor, string Hostname)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, vendor, hostname FROM hosts WHERE os_guess = ''";
                using var r = cmd.ExecuteReader();
                while (r.Read()) pending.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
            }
            foreach (var (id, vendor, hostname) in pending)
            {
                var guess = OsFingerprint.Passive(vendor, hostname);
                if (guess == "") continue;
                using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE hosts SET os_guess = $g WHERE id = $id";
                upd.Parameters.AddWithValue("$g", guess);
                upd.Parameters.AddWithValue("$id", id);
                upd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// Records what a device announced about itself over mDNS, matched by the
    /// address the announcement came from. The name goes in its own column and
    /// is only ever a fallback for display; services accumulate, and refine the
    /// device guess when the current one is empty or came only from the vendor.
    /// Returns true when the row actually changed.
    /// </summary>
    public bool ApplyMdns(string ip, string name, IEnumerable<string> services)
    {
        lock (_lock)
        {
            using var conn = Open();
            long id; string curName, curServices, curGuess;
            using (var find = conn.CreateCommand())
            {
                find.CommandText = "SELECT id, mdns_name, mdns_services, os_guess FROM hosts WHERE ip = $ip AND forgotten = 0 ORDER BY last_seen DESC LIMIT 1";
                find.Parameters.AddWithValue("$ip", ip);
                using var r = find.ExecuteReader();
                if (!r.Read()) return false;
                id = r.GetInt64(0); curName = r.GetString(1); curServices = r.GetString(2); curGuess = r.GetString(3);
            }

            var merged = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in curServices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) merged.Add(s);
            foreach (var s in services) if (!string.IsNullOrWhiteSpace(s)) merged.Add(s.Trim());
            var newServices = string.Join(",", merged);
            var newName = string.IsNullOrWhiteSpace(name) ? curName : name.Trim();

            var newGuess = curGuess;
            if (merged.Count > 0 && (curGuess == "" || curGuess.EndsWith("(vendor)", StringComparison.Ordinal)))
            {
                var fromServices = OsFingerprint.FromServices(merged);
                if (fromServices != "") newGuess = fromServices;
            }

            if (newName == curName && newServices == curServices && newGuess == curGuess) return false;

            using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE hosts SET mdns_name = $n, mdns_services = $s, os_guess = $g WHERE id = $id";
            upd.Parameters.AddWithValue("$n", newName);
            upd.Parameters.AddWithValue("$s", newServices);
            upd.Parameters.AddWithValue("$g", newGuess);
            upd.Parameters.AddWithValue("$id", id);
            return upd.ExecuteNonQuery() > 0;
        }
    }

    public bool SetKnown(long id, bool known)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE hosts SET known = $known WHERE id = $id";
            cmd.Parameters.AddWithValue("$known", known ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Soft-delete: mark forgotten (also drops watch so it can't alert).</summary>
    public bool SetForgotten(long id, bool forgotten)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = forgotten
                ? "UPDATE hosts SET forgotten = 1, watched = 0 WHERE id = $id"
                : "UPDATE hosts SET forgotten = 0 WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Hard-delete: remove the host and its events for good.</summary>
    public bool DeletePermanent(long id)
    {
        lock (_lock)
        {
            using var conn = Open();
            using (var ev = conn.CreateCommand())
            {
                ev.CommandText = "DELETE FROM events WHERE host_id = $id";
                ev.Parameters.AddWithValue("$id", id);
                ev.ExecuteNonQuery();
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM hosts WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }
}
