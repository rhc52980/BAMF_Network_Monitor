namespace LanWatch.Services;

public partial class HostStore
{
    /// <summary>How many online/offline events are kept: the bulk of what the database holds, and what the history retention prunes.</summary>
    public long CountEvents()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM events";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }
}
