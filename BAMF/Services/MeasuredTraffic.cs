namespace LanWatch.Services;

/// <summary>
/// Traffic counted by something that sees it all: a managed switch's port
/// counters, or the router's own count per client. The capture in
/// <see cref="TrafficMonitor"/> only sees what reaches this machine, which on a
/// switched network is its own traffic and broadcast. A switch port or the
/// router sees every byte a device sends and gets, so where one of them counts
/// a device, its figure replaces the capture's.
///
/// Readers report bytes counted since their last look; this turns them into
/// rates, running totals and the hourly history, the same shapes the capture
/// gives, so everything that shows traffic takes either.
/// </summary>
public sealed class MeasuredTraffic
{
    /// <summary>One device's latest count. Source is "switch" or "router"; Where says which one, e.g. "Office switch, port 5".</summary>
    public sealed record Reading(string Source, string Where, double Rx, double Tx, long RxTotal, long TxTotal, DateTime At, IReadOnlyList<long> Strip);

    private sealed class Entry
    {
        public string Source = "", Where = "";
        public double Rx, Tx;
        public long RxTotal, TxTotal, RxHour, TxHour;
        public DateTime At;
        public readonly long[] Strip = new long[TrafficMonitor.StripLength];
        public int StripAt;
    }

    private readonly HostStore _store;
    private readonly ILogger<MeasuredTraffic> _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _byMac = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastFlush = DateTime.UtcNow;

    /// <summary>A reading older than this is stale: its switch or router stopped answering.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);

    public MeasuredTraffic(HostStore store, ILogger<MeasuredTraffic> log) { _store = store; _log = log; }

    /// <summary>
    /// Bytes a device got (rx) and sent (tx) over the last few seconds, from one
    /// source. Rx is always "to the device": a switch port's out-counter, the
    /// router's sent-to-client count.
    /// </summary>
    public void Report(string mac, string source, string where, long rxBytes, long txBytes, double seconds)
    {
        if (mac == "" || seconds <= 0 || rxBytes < 0 || txBytes < 0) return;
        lock (_lock)
        {
            if (!_byMac.TryGetValue(mac, out var e)) _byMac[mac] = e = new Entry();
            // A device counted by both the switch and the router: the switch
            // wins, since it's the wire itself.
            if (e.Source == "switch" && source != "switch" && DateTime.UtcNow - e.At < FreshFor) return;
            e.Source = source; e.Where = where;
            e.Rx = rxBytes / seconds; e.Tx = txBytes / seconds;
            e.RxTotal += rxBytes; e.TxTotal += txBytes;
            e.RxHour += rxBytes; e.TxHour += txBytes;
            e.Strip[e.StripAt] = rxBytes + txBytes;
            e.StripAt = (e.StripAt + 1) % e.Strip.Length;
            e.At = DateTime.UtcNow;
        }
        if (DateTime.UtcNow - _lastFlush > TimeSpan.FromMinutes(5)) Flush();
    }

    /// <summary>True while a switch or the router is counting this device, so the capture's figure isn't also kept.</summary>
    public bool Covers(string mac)
    {
        lock (_lock) return _byMac.TryGetValue(mac, out var e) && DateTime.UtcNow - e.At < FreshFor;
    }

    /// <summary>The fresh readings, by MAC.</summary>
    public Dictionary<string, Reading> Fresh()
    {
        var now = DateTime.UtcNow;
        lock (_lock)
            return _byMac.Where(kv => now - kv.Value.At < FreshFor).ToDictionary(kv => kv.Key, kv =>
            {
                var e = kv.Value;
                var strip = new long[e.Strip.Length];
                for (var i = 0; i < strip.Length; i++) strip[i] = e.Strip[(e.StripAt + i) % strip.Length];
                return new Reading(e.Source, e.Where, e.Rx, e.Tx, e.RxTotal, e.TxTotal, e.At, strip);
            }, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The capture's counters with these on top: a device a switch or the
    /// router counts takes their figure instead of the capture's.
    /// </summary>
    public Dictionary<string, TrafficMonitor.Counter> Overlay(Dictionary<string, TrafficMonitor.Counter> captured)
    {
        var map = new Dictionary<string, TrafficMonitor.Counter>(captured, StringComparer.OrdinalIgnoreCase);
        foreach (var (mac, r) in Fresh()) map[mac] = new TrafficMonitor.Counter(r.RxTotal, r.TxTotal, r.Rx, r.Tx, r.Strip);
        return map;
    }

    /// <summary>What's been counted since the last flush goes into the hourly history, like the capture's.</summary>
    public void Flush()
    {
        var hour = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:00:00Z");
        var rows = new List<(string, string, long, long)>();
        lock (_lock)
        {
            _lastFlush = DateTime.UtcNow;
            foreach (var (mac, e) in _byMac)
            {
                if (e.RxHour == 0 && e.TxHour == 0) continue;
                rows.Add((mac, hour, e.RxHour, e.TxHour));
                e.RxHour = 0; e.TxHour = 0;
            }
        }
        try { _store.AddTraffic(rows); } catch (Exception ex) { _log.LogDebug(ex, "Measured traffic history write failed"); }
    }
}
