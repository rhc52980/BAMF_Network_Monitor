using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// Tidying up devices that have been gone a long time: a guest's phone from the spring, an old tablet. Off by default.
/// Only a device nobody has taken any notice of is ever touched: not known, not watched, not ignored, with no name,
/// note, link, tag, type, Map icon, switch port, floor-plan place or network cards of its own, and not a switch or router
/// record. It is forgotten (the Forgotten tab's soft delete), never deleted, so it can be brought back, and it comes
/// back by itself, with its history, if it ever turns up again (a forgotten device that is seen again is no longer forgotten).
/// </summary>
public partial class HostStore
{
    public const int MinTidyDays = 14, MaxTidyDays = 730, DefaultTidyDays = 60;
    private const string TidyLastSetting = "tidyLast";

    public bool TidyEnabled => GetSetting("tidyEnabled") == "true";
    public int TidyDays => int.TryParse(GetSetting("tidyDays"), out var d) && d is >= MinTidyDays and <= MaxTidyDays ? d : DefaultTidyDays;

    public sealed record TidyResult(string At, int Count, List<string> Names);

    private const string StaleWhere = """
        h.forgotten = 0 AND h.ignored = 0 AND h.known = 0 AND h.watched = 0 AND h.online = 0
        AND h.custom_name = '' AND h.note = '' AND h.link = '' AND h.last_seen < $cut
        AND NOT EXISTS (SELECT 1 FROM host_tags t WHERE t.host_id = h.id)
        AND NOT EXISTS (SELECT 1 FROM device_types d WHERE d.host_id = h.id)
        AND NOT EXISTS (SELECT 1 FROM device_kinds k WHERE k.host_id = h.id)
        AND NOT EXISTS (SELECT 1 FROM placements p WHERE p.host_id = h.id)
        AND NOT EXISTS (SELECT 1 FROM floor_places f WHERE f.host_id = h.id)
        AND NOT EXISTS (SELECT 1 FROM switches s WHERE s.host_id = h.id)
        AND NOT EXISTS (SELECT 1 FROM host_interfaces i WHERE i.host_id = h.id OR i.parent_id = h.id)
        """;

    /// <summary>The devices that would be tidied: gone for more than <paramref name="days"/> days and untouched, oldest first.</summary>
    public List<HostRecord> StaleDevices(int days, DateTime nowUtc)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT h.id FROM hosts h WHERE {StaleWhere} ORDER BY h.last_seen";
            cmd.Parameters.AddWithValue("$cut", nowUtc.AddDays(-days).ToString("o"));
            var ids = new HashSet<long>();
            using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetInt64(0));
            return GetAll().Where(h => ids.Contains(h.Id)).OrderBy(h => h.LastSeen, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Forgets them, and writes down what was done.</summary>
    public TidyResult TidyStale(int days, DateTime nowUtc)
    {
        var stale = StaleDevices(days, nowUtc);
        foreach (var h in stale) SetForgotten(h.Id, true);
        var names = stale.Select(h => !string.IsNullOrEmpty(h.CustomName) ? h.CustomName : !string.IsNullOrEmpty(h.Hostname) && h.Hostname != "—" ? h.Hostname : h.Ip).Take(30).ToList();
        var result = new TidyResult(nowUtc.ToString("o"), stale.Count, names);
        SetSetting(TidyLastSetting, JsonSerializer.Serialize(result));
        return result;
    }

    public TidyResult? LastTidy()
    {
        try { return JsonSerializer.Deserialize<TidyResult>(GetSetting(TidyLastSetting) ?? ""); }
        catch (JsonException) { return null; }
    }
}
