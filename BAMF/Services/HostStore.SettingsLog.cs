using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// The log of settings changes: when, what (by name only), by which password,
/// and from which address. Ages out on the history retention window.
/// </summary>
public partial class HostStore
{
    public sealed record SettingsChange(string At, string What, string Who, string Address);

    private static void InitSettingsLog(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS settings_log (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                at      TEXT NOT NULL,
                what    TEXT NOT NULL,
                who     TEXT NOT NULL,
                address TEXT NOT NULL DEFAULT ''
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void LogSettingsChange(string what, string who, string address)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO settings_log (at, what, who, address) VALUES ($at, $what, $who, $addr)";
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$what", what.Length > 120 ? what[..120] : what);
            cmd.Parameters.AddWithValue("$who", who);
            cmd.Parameters.AddWithValue("$addr", address.Length > 64 ? address[..64] : address);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The latest changes, newest first.</summary>
    public List<SettingsChange> GetSettingsLog(int limit = 50)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT at, what, who, address FROM settings_log ORDER BY id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 500));
            using var r = cmd.ExecuteReader();
            var list = new List<SettingsChange>();
            while (r.Read()) list.Add(new SettingsChange(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            return list;
        }
    }

    private static void PruneSettingsLog(SqliteConnection conn, string cutoff)
    {
        using var prune = conn.CreateCommand();
        prune.CommandText = "DELETE FROM settings_log WHERE at < $cutoff";
        prune.Parameters.AddWithValue("$cutoff", cutoff);
        try { prune.ExecuteNonQuery(); } catch (SqliteException) { /* not created yet on first Init */ }
    }
}
