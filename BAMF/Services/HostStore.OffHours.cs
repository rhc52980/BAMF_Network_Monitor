using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// The hours each device should be off (see <see cref="OffHours"/>). Kept in the settings table as one small JSON map: a handful of devices at most.
/// </summary>
public partial class HostStore
{
    private readonly object _offHoursLock = new();

    /// <summary>Alert when a device is found on in its off hours. On unless switched off; it only ever concerns devices given hours.</summary>
    public bool OffHoursAlertEnabled => GetSetting("offHoursAlert") != "false";

    private Dictionary<long, OffWindow> ReadOffHours()
    {
        try { return JsonSerializer.Deserialize<Dictionary<long, OffWindow>>(GetSetting("offHours") ?? "{}") ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>host id -> the hours it should be off.</summary>
    public Dictionary<long, OffWindow> GetOffHours()
    {
        lock (_offHoursLock) return ReadOffHours();
    }

    /// <summary>Sets a device's off hours, or clears them (<paramref name="window"/> null). Returns what is wrong, or null.</summary>
    public string? SetOffHours(long hostId, OffWindow? window)
    {
        if (window is not null && OffHours.Problem(window.From, window.To, window.Days) is { } problem) return problem;
        lock (_offHoursLock)
        {
            if (GetAll().All(h => h.Id != hostId)) return "That device no longer exists.";
            var map = ReadOffHours();
            if (window is null) map.Remove(hostId); else map[hostId] = window;
            SetSetting("offHours", JsonSerializer.Serialize(map));
        }
        return null;
    }
}
