using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>Online and offline events across every device since a moment, for the 3D view's replay of the day.</summary>
public partial class HostStore
{
    public sealed record TimelineEvent(long HostId, string Type, string At);

    /// <summary>The events of devices that aren't ignored or forgotten, oldest first.</summary>
    public List<TimelineEvent> GetEventsSince(DateTime sinceUtc)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT e.host_id, e.type, e.at FROM events e JOIN hosts h ON h.id = e.host_id
                WHERE h.ignored = 0 AND h.forgotten = 0 AND e.at >= $s
                ORDER BY e.at
                """;
            cmd.Parameters.AddWithValue("$s", sinceUtc.ToString("o"));
            using var r = cmd.ExecuteReader();
            var list = new List<TimelineEvent>();
            while (r.Read()) list.Add(new TimelineEvent(r.GetInt64(0), r.GetString(1), r.GetString(2)));
            return list;
        }
    }
}
