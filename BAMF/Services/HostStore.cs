using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

public record HostRecord(
    long Id, string Mac, string Ip, string Hostname, string CustomName, string Vendor, string Subnet,
    bool Online, bool Known, bool Ignored, bool Watched, bool Forgotten, string Note, string FirstSeen, string LastSeen,
    string OsGuess);

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
                os_guess    TEXT NOT NULL DEFAULT ''
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

        PruneEventsInternal(conn);

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
    }

    /// <summary>Deletes events older than the configured retention window.</summary>
    public void PruneEvents()
    {
        lock (_lock)
        {
            using var conn = Open();
            PruneEventsInternal(conn);
        }
    }

    private void PruneEventsInternal(SqliteConnection conn)
    {
        using var prune = conn.CreateCommand();
        prune.CommandText = "DELETE FROM events WHERE at < $cutoff";
        prune.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddDays(-_retentionDays).ToString("o"));
        prune.ExecuteNonQuery();
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
                check.CommandText = "SELECT id, hostname, online, ignored, watched FROM hosts WHERE mac = $mac";
                check.Parameters.AddWithValue("$mac", mac);
                using var r = check.ExecuteReader();
                if (r.Read())
                {
                    var hostId = r.GetInt64(0);
                    var existingHostname = r.GetString(1);
                    var wasOnline = r.GetInt64(2) == 1;
                    var isIgnored = r.GetInt64(3) == 1;
                    var isWatched = r.GetInt64(4) == 1;
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
                            subnet = $subnet, online = 1, forgotten = 0, last_seen = $now
                        WHERE mac = $mac
                        """;
                    upd.Parameters.AddWithValue("$ip", ip);
                    upd.Parameters.AddWithValue("$hostname", newHostname);
                    upd.Parameters.AddWithValue("$vendor", vendor);
                    upd.Parameters.AddWithValue("$subnet", subnet);
                    upd.Parameters.AddWithValue("$now", now);
                    upd.Parameters.AddWithValue("$mac", mac);
                    upd.ExecuteNonQuery();

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

            if (!autoIgnore)
            {
                using var lastId = conn.CreateCommand();
                lastId.CommandText = "SELECT last_insert_rowid()";
                var newId = (long)lastId.ExecuteScalar()!;
                AddEvent(conn, newId, "online", now);
            }
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
    public List<HostRecord> MarkOffline(IReadOnlySet<string> seenMacs)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow.ToString("o");
            var wentDown = new List<HostRecord>();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, mac, ignored, watched FROM hosts WHERE online = 1";
            var toMark = new List<(long Id, string Mac, bool Ignored, bool Watched)>();
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var id = r.GetInt64(0);
                    var mac = r.GetString(1);
                    var ign = r.GetInt64(2) == 1;
                    var wat = r.GetInt64(3) == 1;
                    if (!seenMacs.Contains(mac)) toMark.Add((id, mac, ign, wat));
                }

            foreach (var (id, mac, ign, wat) in toMark)
            {
                using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE hosts SET online = 0 WHERE mac = $mac";
                upd.Parameters.AddWithValue("$mac", mac);
                upd.ExecuteNonQuery();
                if (!ign) AddEvent(conn, id, "offline", now);
                if (wat && !ign) wentDown.Add(GetByIdInternal(conn, id));
            }
            return wentDown.Where(h => h is not null).ToList()!;
        }
    }

    private HostRecord? GetByIdInternal(SqliteConnection conn, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, mac, ip, hostname, custom_name, vendor, subnet, online, known, ignored, watched, forgotten, note, first_seen, last_seen, os_guess
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
            r.GetString(15));
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
                SELECT id, mac, ip, hostname, custom_name, vendor, subnet, online, known, ignored, watched, forgotten, note, first_seen, last_seen, os_guess
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
                    r.GetString(15)));
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
