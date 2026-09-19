using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// Tags on devices: "kids", "IoT", "work", whatever groups your network into.
/// A device can carry several. The dashboard filters the list and the Map by
/// tag, and the Who's home board can show one group at a time.
/// </summary>
public partial class HostStore
{
    public const int MaxTagsPerHost = 20;
    public const int MaxTagLength = 24;

    private static void InitTags(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS host_tags (
                host_id INTEGER NOT NULL,
                tag     TEXT NOT NULL,
                PRIMARY KEY (host_id, tag)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>host id -> its tags, in the order they were given.</summary>
    public Dictionary<long, List<string>> GetTags()
    {
        lock (_lock)
        {
            using var conn = Open();
            return GetTagsInternal(conn);
        }
    }

    private static Dictionary<long, List<string>> GetTagsInternal(SqliteConnection conn)
    {
        var map = new Dictionary<long, List<string>>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT host_id, tag FROM host_tags ORDER BY rowid";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!map.TryGetValue(r.GetInt64(0), out var list)) map[r.GetInt64(0)] = list = new();
            list.Add(r.GetString(1));
        }
        return map;
    }

    /// <summary>Replaces a device's tags. Returns an error for the user, or null.</summary>
    public string? SetTags(long hostId, IEnumerable<string> tags)
    {
        var clean = new List<string>();
        foreach (var raw in tags)
        {
            var t = (raw ?? "").Trim();
            if (t.Length == 0) continue;
            if (t.Length > MaxTagLength) return $"Tags can be at most {MaxTagLength} characters.";
            if (t.Contains(',')) return "A tag can't contain a comma.";
            if (!clean.Any(c => string.Equals(c, t, StringComparison.OrdinalIgnoreCase))) clean.Add(t);
        }
        if (clean.Count > MaxTagsPerHost) return $"At most {MaxTagsPerHost} tags on a device.";
        lock (_lock)
        {
            using var conn = Open();
            if (GetByIdInternal(conn, hostId) is null) return "That device no longer exists.";
            using var tx = conn.BeginTransaction();
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM host_tags WHERE host_id = $h";
                del.Parameters.AddWithValue("$h", hostId);
                del.ExecuteNonQuery();
            }
            foreach (var t in clean)
            {
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO host_tags (host_id, tag) VALUES ($h, $t)";
                ins.Parameters.AddWithValue("$h", hostId);
                ins.Parameters.AddWithValue("$t", t);
                ins.ExecuteNonQuery();
            }
            tx.Commit();
            return null;
        }
    }
}
