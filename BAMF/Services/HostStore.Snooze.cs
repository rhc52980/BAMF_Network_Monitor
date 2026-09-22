using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// Snoozed devices: "don't tell me about the NAS for two hours" while you
/// reboot it. A snooze holds back the alerts that are about one device - its
/// watch alerts, the alert rules that match it, its port watch - and nothing
/// else. Rule and port alerts still go in the Alerts card, marked as not sent;
/// the offline and online ones are in the device's history as always.
///
/// Each snooze remembers whether the device was online when it started. When
/// it runs out, a watched device that has ended up the other way round gets the
/// alert it would have had, so a reboot that never came back isn't missed.
///
/// Kept in the settings table as one small JSON map, since there are only ever
/// a handful and they expire by themselves.
/// </summary>
public partial class HostStore
{
    public sealed record Snooze(string Until, bool Online);

    /// <summary>The longest a device can be snoozed for: a week.</summary>
    public const int MaxSnoozeMinutes = 7 * 24 * 60;

    private Dictionary<long, Snooze> ReadSnoozes()
    {
        try { return JsonSerializer.Deserialize<Dictionary<long, Snooze>>(GetSetting("snoozes") ?? "{}") ?? new(); }
        catch { return new(); }
    }

    private static DateTime SnoozeEnd(Snooze s) => ParseUtc(s.Until) ?? DateTime.MinValue;

    private void WriteSnoozes(Dictionary<long, Snooze> map) => SetSetting("snoozes", JsonSerializer.Serialize(map));

    /// <summary>host id -> when its snooze ends, for the ones still running.</summary>
    public Dictionary<long, string> GetSnoozes()
    {
        var now = DateTime.UtcNow;
        lock (_snoozeLock)
            return ReadSnoozes().Where(kv => SnoozeEnd(kv.Value) > now).ToDictionary(kv => kv.Key, kv => kv.Value.Until);
    }

    /// <summary>True while this device's alerts are being held back.</summary>
    public bool IsSnoozed(long hostId)
    {
        lock (_snoozeLock)
            return ReadSnoozes().TryGetValue(hostId, out var s) && SnoozeEnd(s) > DateTime.UtcNow;
    }

    /// <summary>Snoozes a device until the given time, remembering how it was when it started.</summary>
    public void SnoozeHost(long hostId, DateTime untilUtc, bool online)
    {
        lock (_snoozeLock)
        {
            var map = ReadSnoozes();
            // Extending a snooze keeps the state it started from.
            var was = map.TryGetValue(hostId, out var old) && SnoozeEnd(old) > DateTime.UtcNow ? old.Online : online;
            map[hostId] = new Snooze(untilUtc.ToString("o"), was);
            WriteSnoozes(map);
        }
    }

    /// <summary>Ends a snooze early. Nothing is sent: you ended it, so you know.</summary>
    public bool Unsnooze(long hostId)
    {
        lock (_snoozeLock)
        {
            var map = ReadSnoozes();
            if (!map.Remove(hostId)) return false;
            WriteSnoozes(map);
            return true;
        }
    }

    /// <summary>The snoozes that have run out since the last call, with the state each started from.</summary>
    public List<(long HostId, bool WasOnline)> DrainExpiredSnoozes()
    {
        lock (_snoozeLock)
        {
            var map = ReadSnoozes();
            var now = DateTime.UtcNow;
            var done = map.Where(kv => SnoozeEnd(kv.Value) <= now).Select(kv => (kv.Key, kv.Value.Online)).ToList();
            if (done.Count == 0) return done;
            foreach (var (id, _) in done) map.Remove(id);
            WriteSnoozes(map);
            return done;
        }
    }

    private readonly object _snoozeLock = new();
}
