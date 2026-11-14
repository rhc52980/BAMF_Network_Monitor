namespace LanWatch.Services;

/// <summary>
/// Which alerts someone has acknowledged, and which have been escalated. Kept apart from the alerts themselves, because some alerts live
/// in the database and some in the traffic monitor's own list: each is named by a key ("a:12" for the first kind, "t:" and its time, kind
/// and title for the second), and a mark is just that key with the moment.
/// </summary>
public partial class HostStore
{
    public sealed record AlertMark(string? AckedAt, string? EscalatedAt);

    public Dictionary<string, AlertMark> AlertMarks()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, acked_at, escalated_at FROM alert_state";
            var map = new Dictionary<string, AlertMark>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetString(0)] = new AlertMark(r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
            return map;
        }
    }

    /// <summary>Marks these alerts acknowledged now. An alert that already was keeps its first time.</summary>
    public int AcknowledgeAlerts(IEnumerable<string> keys)
    {
        var n = 0;
        var now = DateTime.UtcNow.ToString("o");
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var key in keys.Where(k => k != "").Distinct())
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO alert_state (key, acked_at) VALUES ($k, $at)
                    ON CONFLICT(key) DO UPDATE SET acked_at = COALESCE(acked_at, $at)
                    """;
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$at", now);
                cmd.ExecuteNonQuery();
                n++;
            }
            tx.Commit();
        }
        return n;
    }

    public void MarkAlertEscalated(string key)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO alert_state (key, escalated_at) VALUES ($k, $at)
                ON CONFLICT(key) DO UPDATE SET escalated_at = $at
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }
}
