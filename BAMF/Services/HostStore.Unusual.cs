using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// What the unusual-activity watch noticed, each occurrence once, and the
/// devices someone said were normal for a while. Ages out on the history
/// retention window.
/// </summary>
public partial class HostStore
{
    public sealed record UnusualRow(long Id, long HostId, string Kind, string At, string Title, string Detail, string? ResolvedAt);

    private static void InitUnusual(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS unusual (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                key         TEXT NOT NULL UNIQUE,
                host_id     INTEGER NOT NULL,
                kind        TEXT NOT NULL,
                at          TEXT NOT NULL,
                title       TEXT NOT NULL,
                detail      TEXT NOT NULL,
                resolved_at TEXT
            );
            CREATE TABLE IF NOT EXISTS unusual_mutes (
                host_id INTEGER NOT NULL,
                kind    TEXT NOT NULL,
                until   TEXT NOT NULL,
                PRIMARY KEY (host_id, kind)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Records a finding unless that occurrence is already there. True when it's new.</summary>
    public bool AddUnusual(Unusual.Finding f)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            var now = DateTime.UtcNow.ToString("o");
            cmd.CommandText = "INSERT OR IGNORE INTO unusual (key, host_id, kind, at, title, detail, resolved_at) VALUES ($k, $h, $kind, $at, $t, $d, $r)";
            cmd.Parameters.AddWithValue("$k", f.Key);
            cmd.Parameters.AddWithValue("$h", f.HostId);
            cmd.Parameters.AddWithValue("$kind", f.Kind);
            cmd.Parameters.AddWithValue("$at", now);
            cmd.Parameters.AddWithValue("$t", f.Title);
            cmd.Parameters.AddWithValue("$d", f.Detail);
            cmd.Parameters.AddWithValue("$r", f.Ongoing ? DBNull.Value : now);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>Brings an open finding's title and detail up to date, as an outage grows.</summary>
    public void UpdateUnusual(string key, string title, string detail)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE unusual SET title = $t, detail = $d WHERE key = $k AND resolved_at IS NULL";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$t", title);
            cmd.Parameters.AddWithValue("$d", detail);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The open ones, by their key.</summary>
    public Dictionary<string, UnusualRow> OpenUnusual()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, id, host_id, kind, at, title, detail FROM unusual WHERE resolved_at IS NULL";
            using var r = cmd.ExecuteReader();
            var open = new Dictionary<string, UnusualRow>();
            while (r.Read())
                open[r.GetString(0)] = new UnusualRow(r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), null);
            return open;
        }
    }

    public void ResolveUnusual(long id)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE unusual SET resolved_at = $now WHERE id = $id AND resolved_at IS NULL";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Open ones, and any from the last <paramref name="hours"/>, newest first.</summary>
    public List<UnusualRow> RecentUnusual(int hours = 24)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, host_id, kind, at, title, detail, resolved_at FROM unusual WHERE resolved_at IS NULL OR at >= $since ORDER BY at DESC LIMIT 50";
            cmd.Parameters.AddWithValue("$since", DateTime.UtcNow.AddHours(-hours).ToString("o"));
            using var r = cmd.ExecuteReader();
            var list = new List<UnusualRow>();
            while (r.Read())
                list.Add(new UnusualRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
            return list;
        }
    }

    public UnusualRow? GetUnusual(long id) => RecentUnusual(24 * 365).FirstOrDefault(u => u.Id == id);

    /// <summary>"That's normal": this kind of finding isn't raised for this device for a while.</summary>
    public void MuteUnusual(long hostId, string kind, TimeSpan forHow)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO unusual_mutes (host_id, kind, until) VALUES ($h, $k, $u) ON CONFLICT(host_id, kind) DO UPDATE SET until = $u";
            cmd.Parameters.AddWithValue("$h", hostId);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.Add(forHow).ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The (device, kind) pairs muted now.</summary>
    public HashSet<(long, string)> UnusualMutes()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT host_id, kind FROM unusual_mutes WHERE until > $now";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            using var r = cmd.ExecuteReader();
            var set = new HashSet<(long, string)>();
            while (r.Read()) set.Add((r.GetInt64(0), r.GetString(1)));
            return set;
        }
    }

    /// <summary>Every device's online/offline events since a time, oldest first.</summary>
    public Dictionary<long, List<(DateTime At, bool Online)>> EventsSince(DateTime sinceUtc)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT host_id, at, type FROM events WHERE at >= $since ORDER BY host_id, at";
            cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("o"));
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<long, List<(DateTime, bool)>>();
            while (r.Read())
            {
                if (!DateTime.TryParse(r.GetString(1), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)) continue;
                var id = r.GetInt64(0);
                if (!map.TryGetValue(id, out var list)) map[id] = list = new();
                list.Add((at, r.GetString(2) == "online"));
            }
            return map;
        }
    }

    /// <summary>Every device's ping times between two times, oldest first, without the ones that got no reply.</summary>
    public Dictionary<long, List<int>> LatencySince(DateTime sinceUtc, DateTime untilUtc)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT host_id, ms FROM latency WHERE at >= $since AND at < $until AND ms IS NOT NULL ORDER BY host_id, at";
            cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$until", untilUtc.ToString("o"));
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<long, List<int>>();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                if (!map.TryGetValue(id, out var list)) map[id] = list = new();
                list.Add(r.GetInt32(1));
            }
            return map;
        }
    }

    private static void PruneUnusual(SqliteConnection conn, string cutoff)
    {
        foreach (var sql in new[] { "DELETE FROM unusual WHERE at < $cutoff AND resolved_at IS NOT NULL", "DELETE FROM unusual_mutes WHERE until < $cutoff" })
        {
            using var prune = conn.CreateCommand();
            prune.CommandText = sql;
            prune.Parameters.AddWithValue("$cutoff", cutoff);
            try { prune.ExecuteNonQuery(); } catch (SqliteException) { /* not created yet on first Init */ }
        }
    }
}
