namespace LanWatch.Services;

/// <summary>
/// Every five minutes, looks at each device's history for what isn't normal
/// for it (see <see cref="Unusual"/>), records each occurrence once, and sends
/// it as an "unusual" alert to the destinations that take those, through
/// quiet hours like any other. Something ongoing (a device off, or slow) stays
/// open until it's over. On by default; Settings → Alerts switches it off.
/// </summary>
public sealed class UnusualWatch : BackgroundService
{
    public static readonly TimeSpan MuteFor = TimeSpan.FromDays(30);

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly ILogger<UnusualWatch> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private Dictionary<long, double> _usualMs = new();
    private DateTime _usualAt = DateTime.MinValue;

    public UnusualWatch(HostStore store, ScannerService scanner, ILogger<UnusualWatch> log)
    {
        _store = store; _scanner = scanner; _log = log;
    }

    public bool Enabled => _store.GetSetting("unusualWatch") != "false";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { if (Enabled) await Check(DateTime.UtcNow, TimeZoneInfo.Local, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Unusual activity check failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>How many tracked devices have the two weeks of history it needs, and how many are still learning.</summary>
    public (int Watching, int Learning) Coverage(DateTime now)
    {
        var hosts = _store.GetAll().Where(h => !h.Ignored && !h.Forgotten).ToList();
        var learned = hosts.Count(h => FirstSeen(h) is { } f && now - f >= TimeSpan.FromDays(Unusual.LearnDays));
        return (learned, hosts.Count - learned);
    }

    private static DateTime? FirstSeen(HostRecord h) =>
        DateTime.TryParse(h.FirstSeen, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var f) ? f : null;

    private static string NameOf(HostRecord h) => h.CustomName != "" ? h.CustomName : h.Hostname != "" ? h.Hostname : h.Ip;

    /// <summary>One look at every device. Returns the findings that were new.</summary>
    public async Task<List<Unusual.Finding>> Check(DateTime now, TimeZoneInfo tz, CancellationToken ct)
    {
        await _busy.WaitAsync(ct);
        try
        {
            var hosts = _store.GetAll().Where(h => !h.Ignored && !h.Forgotten).ToList();
            var events = _store.EventsSince(now.AddDays(-(Unusual.WindowDays + 1)));
            // Each device's usual ping time: the last week but the last hour, worked out hourly.
            if (now - _usualAt > TimeSpan.FromHours(1))
            {
                _usualMs = _store.LatencySince(now.AddDays(-7), now.AddHours(-1))
                    .Where(kv => kv.Value.Count >= 200).ToDictionary(kv => kv.Key, kv => Unusual.Median(kv.Value)!.Value);
                _usualAt = now;
            }
            var recent = _store.LatencySince(now.AddMinutes(-15), now.AddMinutes(1));
            var mutes = _store.UnusualMutes();
            var open = _store.OpenUnusual();

            var found = new List<Unusual.Finding>();
            foreach (var h in hosts)
            {
                if (FirstSeen(h) is not { } first) continue;
                var d = new Unusual.History(h.Id, NameOf(h), first, h.Online, h.Watched,
                    events.TryGetValue(h.Id, out var e) ? e : []);
                if (Unusual.OffTooLong(d, now) is { } off) found.Add(off);
                if (Unusual.OddHour(d, now, tz) is { } odd) found.Add(odd);
                var lastFive = recent.TryGetValue(h.Id, out var r) ? r.TakeLast(5).ToList() : [];
                double? usual = _usualMs.TryGetValue(h.Id, out var u) ? u : null;
                if (h.Online && Unusual.Slow(h.Id, NameOf(h), usual, lastFive) is { } slow) found.Add(slow);
            }

            var fresh = new List<Unusual.Finding>();
            foreach (var f in found)
            {
                if (mutes.Contains((f.HostId, f.Kind))) continue;
                if (open.ContainsKey(f.Key)) { _store.UpdateUnusual(f.Key, f.Title, f.Detail); continue; }
                if (!_store.AddUnusual(f)) continue;   // already noted
                fresh.Add(f);
                await _scanner.SendGenericAlert(f.Title, f.Detail, "unusual", ct);
            }

            // What's over: a device back on, or back to its usual speed, or now
            // muted, or gone (ignored, forgotten). A device still off stays open
            // however long that goes on.
            var keys = found.Select(f => f.Key).ToHashSet();
            var byId = hosts.ToDictionary(h => h.Id);
            foreach (var (key, row) in open)
            {
                var muted = mutes.Contains((row.HostId, row.Kind));
                if (keys.Contains(key) && !muted) continue;
                if (row.Kind == "offline" && !muted && byId.TryGetValue(row.HostId, out var off) && !off.Online) continue;
                if (row.Kind == "slow" && byId.TryGetValue(row.HostId, out var h) && h.Online && !mutes.Contains((row.HostId, "slow")))
                {
                    var lastFive = recent.TryGetValue(row.HostId, out var r) ? r.TakeLast(5).ToList() : [];
                    if (!Unusual.SlowOver(_usualMs.TryGetValue(row.HostId, out var u) ? u : null, lastFive)) continue;
                }
                _store.ResolveUnusual(row.Id);
            }
            if (fresh.Count > 0) _log.LogInformation("Unusual activity: {Titles}", string.Join("; ", fresh.Select(f => f.Title)));
            return fresh;
        }
        finally { _busy.Release(); }
    }
}
