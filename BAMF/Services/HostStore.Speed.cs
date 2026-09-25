using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// Speed test results: one row per test, kept for a year. A test that failed
/// is written down too, with why, so the schedule knows it has had its go and
/// the card can say what went wrong.
/// </summary>
public partial class HostStore
{
    public sealed record SpeedResult(string At, double DownMbps, double UpMbps, int PingMs, int JitterMs,
        string Server, int MegabytesUsed, bool Manual, string? Error);

    private static void InitSpeed(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS speed_tests (
                at      TEXT PRIMARY KEY,
                down    REAL NOT NULL,
                up      REAL NOT NULL,
                ping    INTEGER NOT NULL,
                jitter  INTEGER NOT NULL,
                server  TEXT NOT NULL,
                mb      INTEGER NOT NULL,
                manual  INTEGER NOT NULL,
                error   TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void AddSpeedResult(SpeedResult r)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO speed_tests (at, down, up, ping, jitter, server, mb, manual, error)
                VALUES ($at, $d, $u, $p, $j, $s, $mb, $m, $e);
                DELETE FROM speed_tests WHERE at < $old;
                """;
            cmd.Parameters.AddWithValue("$at", r.At);
            cmd.Parameters.AddWithValue("$d", r.DownMbps);
            cmd.Parameters.AddWithValue("$u", r.UpMbps);
            cmd.Parameters.AddWithValue("$p", r.PingMs);
            cmd.Parameters.AddWithValue("$j", r.JitterMs);
            cmd.Parameters.AddWithValue("$s", r.Server);
            cmd.Parameters.AddWithValue("$mb", r.MegabytesUsed);
            cmd.Parameters.AddWithValue("$m", r.Manual ? 1 : 0);
            cmd.Parameters.AddWithValue("$e", (object?)r.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$old", DateTime.UtcNow.AddDays(-366).ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Tests over the last so many days, oldest first, failed ones included.</summary>
    public List<SpeedResult> GetSpeedResults(int days)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT at, down, up, ping, jitter, server, mb, manual, error FROM speed_tests WHERE at >= $from ORDER BY at";
            cmd.Parameters.AddWithValue("$from", DateTime.UtcNow.AddDays(-days).ToString("o"));
            var list = new List<SpeedResult>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new SpeedResult(r.GetString(0), r.GetDouble(1), r.GetDouble(2), r.GetInt32(3), r.GetInt32(4),
                    r.GetString(5), r.GetInt32(6), r.GetInt32(7) != 0, r.IsDBNull(8) ? null : r.GetString(8)));
            return list;
        }
    }
}
