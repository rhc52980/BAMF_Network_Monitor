namespace LanWatch.Services;

/// <summary>What alert routing needs to know about a device: its tags, and which device a MAC address is.</summary>
public partial class HostStore
{
    /// <summary>A device's tags, any case, for matching against a destination's. Empty for a device with none.</summary>
    public HashSet<string> TagsOf(long hostId)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT tag FROM host_tags WHERE host_id = $h";
            cmd.Parameters.AddWithValue("$h", hostId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(0));
        }
        return set;
    }

    /// <summary>The device with this MAC address, or null.</summary>
    public long? HostIdOfMac(string mac) =>
        GetAll().FirstOrDefault(h => string.Equals(h.Mac, mac, StringComparison.OrdinalIgnoreCase))?.Id;
}
