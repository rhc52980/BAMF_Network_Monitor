namespace LanWatch.Services;

public partial class HostStore
{
    /// <summary>How many times each device went offline since a moment.</summary>
    public Dictionary<long, int> OfflineCounts(DateTime sinceUtc)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT host_id, COUNT(*) FROM events WHERE type = 'offline' AND at >= $since GROUP BY host_id";
            cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("o"));
            var map = new Dictionary<long, int>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetInt64(0)] = r.GetInt32(1);
            return map;
        }
    }
}
