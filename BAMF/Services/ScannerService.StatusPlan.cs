namespace LanWatch.Services;

/// <summary>
/// How the alerts for watched devices changing state in one scan are sent: gathered under the switch that took them down, and
/// calmed when a device keeps dropping. Both are on by default except holding a flapping device's own alerts back, which you choose.
/// </summary>
public partial class ScannerService
{
    /// <summary>Alerts for devices behind a switch that went offline are one alert under the switch. On unless switched off.</summary>
    public bool CascadeEnabled => _store.GetSetting("cascadeAlert") != "false";

    /// <summary>One alert when a device keeps dropping. On unless switched off.</summary>
    public bool FlapAlertEnabled => _store.GetSetting("flapAlert") != "false";

    /// <summary>While a device flaps, its own offline and back-online alerts are held back (and one is sent if it stays down). Off unless chosen.</summary>
    public bool FlapHold => _store.GetSetting("flapHold") == "true";

    public const int DefaultFlapDrops = 4, MinFlapDrops = 3, MaxFlapDrops = 20;
    public static readonly TimeSpan FlapWindow = TimeSpan.FromHours(1), FlapStillDown = TimeSpan.FromMinutes(15);

    /// <summary>How many times a device must have gone offline in the last hour to be called flapping.</summary>
    public int FlapDrops => int.TryParse(_store.GetSetting("flapDrops"), out var n) && n is >= MinFlapDrops and <= MaxFlapDrops ? n : DefaultFlapDrops;

    private readonly Dictionary<long, DateTime> _flapAlertedAt = new();     // when each device's flapping was last said
    private readonly Dictionary<long, DateTime> _flapHeldSince = new();     // devices whose offline alert was held while flapping
    private readonly object _flapLock = new();

    private string NameOf(HostRecord h) => h.CustomName != "" ? h.CustomName : h.Hostname is { Length: > 0 } and not "—" ? h.Hostname : h.Ip;

    /// <summary>Sends the alerts for watched devices that went down (or came back) in one scan.</summary>
    internal async Task SendStatusAlerts(IReadOnlyList<HostRecord> changed, bool up, CancellationToken ct, DateTime? nowUtc = null)
    {
        if (changed.Count == 0) return;
        var now = nowUtc ?? DateTime.UtcNow;
        // A snoozed device says nothing, as before, so it isn't counted in a group either.
        var hosts = changed.Where(h => !_store.IsSnoozed(h.Id)).ToList();
        hosts = await CalmFlapping(hosts, up, ct, now);
        if (hosts.Count == 0) return;
        if (!CascadeEnabled)
        {
            foreach (var h in hosts) await SendStatusAlert(h, up, ct);
            return;
        }
        var all = _store.GetAll().ToDictionary(h => h.Id);
        var plan = StatusAlertPlanner.Make(hosts, up, _store.GetSwitches(), _store.GetPlacements(), all);
        foreach (var h in plan.Single) await SendStatusAlert(h, up, ct);
        foreach (var g in plan.Groups)
        {
            var (title, detail) = StatusAlertPlanner.Describe(g, NameOf);
            await SendGenericAlert(title, detail, up ? "status-up" : "status", ct);
        }
    }

    /// <summary>
    /// Devices that keep dropping: one alert saying so, and with <see cref="FlapHold"/> their own offline and back-online alerts held back.
    /// Returns the devices that should still be said one by one.
    /// </summary>
    private async Task<List<HostRecord>> CalmFlapping(List<HostRecord> hosts, bool up, CancellationToken ct, DateTime now)
    {
        if (!FlapAlertEnabled && !FlapHold) return hosts;
        var drops = _store.OfflineCounts(now - FlapWindow);
        var keep = new List<HostRecord>();
        foreach (var h in hosts)
        {
            var n = drops.GetValueOrDefault(h.Id);
            if (n < FlapDrops) { keep.Add(h); continue; }
            if (!up)
            {
                bool say;
                lock (_flapLock)
                {
                    say = FlapAlertEnabled && (!_flapAlertedAt.TryGetValue(h.Id, out var at) || now - at >= FlapWindow);
                    if (say) _flapAlertedAt[h.Id] = now;
                    if (FlapHold) _flapHeldSince.TryAdd(h.Id, now);
                }
                if (say)
                    await SendGenericAlert($"{NameOf(h)} keeps dropping: {n} times in the last hour",
                        $"{NameOf(h)} ({h.Ip}) has gone offline {n} times in the last hour, so it is flapping: a weak signal, a bad cable or a failing power supply do this. " +
                        (FlapHold ? $"Its own offline and back-online alerts are held back while it does, and BAMF will say if it is still offline after {FlapStillDown.TotalMinutes:0} minutes."
                                  : "Each drop still alerts."), "status", ct);
                if (!FlapHold) keep.Add(h);
            }
            else
            {
                lock (_flapLock) _flapHeldSince.Remove(h.Id);
                if (!FlapHold) keep.Add(h);       // held: it came back, so there is nothing left to follow up
            }
        }
        return keep;
    }

    /// <summary>
    /// A device whose offline alert was held while it flapped and that is still offline a while later is said after all, so holding
    /// them back can't hide one that stayed down. Called every minute.
    /// </summary>
    public async Task FollowUpFlaps(CancellationToken ct, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        List<long> due;
        lock (_flapLock)
        {
            due = _flapHeldSince.Where(kv => now - kv.Value >= FlapStillDown).Select(kv => kv.Key).ToList();
            foreach (var id in due) _flapHeldSince.Remove(id);
            foreach (var id in _flapAlertedAt.Where(kv => now - kv.Value > TimeSpan.FromDays(1)).Select(kv => kv.Key).ToList()) _flapAlertedAt.Remove(id);
        }
        if (due.Count == 0) return;
        var all = _store.GetAll().ToDictionary(h => h.Id);
        foreach (var id in due)
            if (all.TryGetValue(id, out var h) && !h.Online && !_store.IsSnoozed(id))
                await SendGenericAlert($"{NameOf(h)} is still offline", $"{NameOf(h)} ({h.Ip}) was flapping and has now been offline for at least {FlapStillDown.TotalMinutes:0} minutes, so this is not a blip.", "status", ct);
    }

    /// <summary>Whether a device's alerts are being held back for flapping right now (for the tests).</summary>
    internal bool IsHeldForFlapping(long hostId) { lock (_flapLock) return _flapHeldSince.ContainsKey(hostId); }
}
