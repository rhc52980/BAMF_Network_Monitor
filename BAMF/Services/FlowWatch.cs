using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace LanWatch.Services;

/// <summary>
/// What the devices are doing, from the packets the traffic monitor hears. Four things come out of it, each with its own switch:
///
/// Where devices talk. Every packet between a device here and an address on the internet is counted against that device and the
/// outside network (a /24) it went to. After a device has been watched for a week, one whose habits are small (it talks to a handful
/// of fixed servers, the way a camera, a plug or a thermostat does) raises an alert the first time it talks to somewhere new. A
/// laptop or a phone talks to hundreds of places and is never called out for one more.
///
/// Scans. A device that touches dozens of addresses on the local network, or dozens of ports on one of them, inside a minute, is
/// scanning: malware looking for a way to spread, or a guest being nosy. One alert, once an hour per device. BAMF's own scans, and
/// the router's, don't count.
///
/// Bandwidth spikes. A device that moved far more in the last day than it usually does (five times its usual day, and at least half
/// a gigabyte) gets one alert: a backup gone wrong, a camera uploading constantly, or something leaving.
///
/// A new device's first week. Seven days after a device first appeared, one alert sums up what it did: how much it was online,
/// what ports it opened, where it talked, how much it moved. Whether the thing you let on the Wi-Fi is behaving.
///
/// What this hears depends on where BAMF's machine sits: on a switched network, its own traffic plus broadcast and multicast; on a
/// mirrored switch port, everything. The dashboard says which. The packet path keeps no locks for long and never touches the database.
/// </summary>
public sealed class FlowWatch : BackgroundService
{
    public const int LearnDays = 7, MaxKnownForAlert = 25;
    public const int ScanAddresses = 40, ScanPorts = 40;
    public static readonly TimeSpan ScanWindow = TimeSpan.FromSeconds(60);
    public const double SpikeFactor = 5.0;
    public const long SpikeFloorBytes = 500L * 1024 * 1024;
    public const int SpikeMinDays = 3;

    private readonly HostStore _store;
    private readonly TrafficMonitor _traffic;
    private readonly ScannerService? _scanner;
    private readonly ILogger<FlowWatch> _log;
    private readonly Func<string, CancellationToken, Task<string?>> _reverse;
    private readonly object _lock = new();

    // where devices talk: mac -> net -> (first seen, bytes and hits since the last flush, a sample address)
    private sealed class Dest { public DateTime First; public long Bytes, Hits; public string Sample = ""; public bool Dirty; }
    private readonly Dictionary<string, Dictionary<string, Dest>> _dests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _firstFlow = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Mac, string Net, string Sample, DateTime At)> _newDest = new();
    private bool _loaded;

    // scans: mac -> recent (time, local destination, port)
    private readonly Dictionary<string, Queue<(DateTime At, string Ip, int Port)>> _recent = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Mac, string What, int Count, string Target, DateTime At)> _scans = new();
    private readonly Dictionary<string, DateTime> _said = new(StringComparer.OrdinalIgnoreCase);
    // scans as they were said, for the 3D view to show as beams for a while: kept in memory, two hours at most
    private readonly List<ScanSeen> _scanLog = new();
    public sealed record ScanSeen(string Mac, string What, int Count, string Target, DateTime At);
    /// <summary>A destination as seen by every device that has talked to it: the network, an address in it, when first and last, how much, and who.</summary>
    public sealed record Destination(string Net, string Sample, string FirstSeen, string LastSeen, long Bytes, List<string> Macs);

    public FlowWatch(HostStore store, TrafficMonitor traffic, ScannerService? scanner, ILogger<FlowWatch> log, Func<string, CancellationToken, Task<string?>>? reverse = null)
    {
        _store = store; _traffic = traffic; _scanner = scanner; _log = log;
        _reverse = reverse ?? ReverseName;
        _traffic.OnIp = Observe;
    }

    public bool Enabled => _store.GetSetting("flowWatch") != "false";
    /// <summary>Noticed but not yet said: scans and new destinations waiting for the next Evaluate. For tests.</summary>
    internal (int Scans, int NewDestinations) Pending { get { lock (_lock) return (_scans.Count, _newDest.Count); } }
    public bool SpikeEnabled => _store.GetSetting("spikeAlert") != "false";
    public bool FirstWeekEnabled => _store.GetSetting("firstWeekReport") != "false";

    // ---------------------------------------------------------------- what an address is

    /// <summary>An address on a home or office network, this machine, a link-local or carrier-NAT one: not the internet.</summary>
    public static bool IsLocal(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return true;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] >= 224;
    }

    /// <summary>The /24 an address is in, as "203.0.113.0/24".</summary>
    public static string NetOf(IPAddress ip) { var b = ip.GetAddressBytes(); return $"{b[0]}.{b[1]}.{b[2]}.0/24"; }

    // ---------------------------------------------------------------- the packet path

    /// <summary>One IPv4 packet. Fast: a dictionary or two, no database, no alerting here.</summary>
    public void Observe(string srcMac, IPAddress src, IPAddress dst, int proto, int dstPort, int len) => Observe(srcMac, src, dst, proto, dstPort, len, DateTime.UtcNow);

    internal void Observe(string srcMac, IPAddress src, IPAddress dst, int proto, int dstPort, int len, DateTime now)
    {
        if (!Enabled) return;
        var srcLocal = IsLocal(src); var dstLocal = IsLocal(dst);
        lock (_lock)
        {
            if (!_loaded) Load();
            if (srcLocal && !dstLocal) Count(srcMac, dst, len, now);
            // A reply from the internet to a device here counts against that device too, under its own MAC. The frame's source is the
            // router, so the device is found by its address; the dashboard's hosts aren't in reach here, so that is left to the scan
            // of local sources, which sees every request a device makes. Requests are enough to know where it talks.
            if (srcLocal && dstLocal && (proto == 6 || proto == 17)) Probe(srcMac, dst.ToString(), dstPort, now);
        }
    }

    private void Count(string mac, IPAddress dst, int len, DateTime now)
    {
        var net = NetOf(dst);
        if (!_dests.TryGetValue(mac, out var nets)) { _dests[mac] = nets = new(); }
        if (!_firstFlow.ContainsKey(mac)) _firstFlow[mac] = now;
        if (!nets.TryGetValue(net, out var d))
        {
            nets[net] = d = new Dest { First = now, Sample = dst.ToString() };
            if (nets.Count > 1) _newDest.Add((mac, net, dst.ToString(), now));
        }
        d.Bytes += len; d.Hits++; d.Dirty = true;
        if (d.Sample.Length == 0) d.Sample = dst.ToString();
    }

    private void Probe(string mac, string dstIp, int port, DateTime now)
    {
        if (!_recent.TryGetValue(mac, out var q)) _recent[mac] = q = new();
        q.Enqueue((now, dstIp, port));
        while (q.Count > 0 && now - q.Peek().At > ScanWindow) q.Dequeue();
        if (q.Count > 4000) q.Dequeue();
        if (q.Count < ScanAddresses) return;
        // Only looked at once a window per device, when it could possibly be over the line.
        var addresses = q.Select(x => x.Ip).Distinct().Count();
        if (addresses >= ScanAddresses) { Note(mac, "addresses", addresses, "", now); return; }
        var byIp = q.GroupBy(x => x.Ip).Select(g => (Ip: g.Key, Ports: g.Select(x => x.Port).Distinct().Count())).OrderByDescending(x => x.Ports).First();
        if (byIp.Ports >= ScanPorts) Note(mac, "ports", byIp.Ports, byIp.Ip, now);
    }

    private void Note(string mac, string what, int count, string target, DateTime now)
    {
        if (_said.TryGetValue("scan:" + mac, out var at) && now - at < TimeSpan.FromHours(1)) return;
        _said["scan:" + mac] = now;
        _scans.Add((mac, what, count, target, now));
    }

    // ---------------------------------------------------------------- the checks

    private void Load()
    {
        _loaded = true;
        foreach (var f in _store.GetFlows())
        {
            if (!_dests.TryGetValue(f.Mac, out var nets)) _dests[f.Mac] = nets = new();
            var first = DateTime.Parse(f.FirstSeen, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
            nets[f.Net] = new Dest { First = first, Sample = f.SampleIp };
            if (!_firstFlow.TryGetValue(f.Mac, out var ff) || first < ff) _firstFlow[f.Mac] = first;
        }
    }

    /// <summary>Writes what was counted since the last flush. Called every five minutes, and when stopping.</summary>
    public void Flush()
    {
        List<HostStore.FlowDelta> deltas;
        lock (_lock)
        {
            if (!_loaded) return;
            deltas = new();
            foreach (var (mac, nets) in _dests)
                foreach (var (net, d) in nets)
                    if (d.Dirty) { deltas.Add(new HostStore.FlowDelta(mac, net, DateTime.UtcNow, d.Bytes, d.Hits, d.Sample)); d.Bytes = 0; d.Hits = 0; d.Dirty = false; }
        }
        try { _store.AddFlows(deltas); } catch (Exception ex) { _log.LogDebug(ex, "Flow history write failed"); }
    }

    private HashSet<string> OwnMacs()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_scanner is null) return set;
        foreach (var p in _scanner.NetworkPlaces().Values) if (p.SelfMac is { Length: > 0 } m) set.Add(m);
        foreach (var g in _scanner.GatewayMacSnapshot().Values) set.Add(g.Mac);
        return set;
    }

    private string NameOf(string mac)
    {
        var h = _store.GetAll().FirstOrDefault(x => string.Equals(x.Mac, mac, StringComparison.OrdinalIgnoreCase));
        return h is null ? mac : $"{(h.CustomName != "" ? h.CustomName : h.Hostname is { Length: > 0 } and not "—" ? h.Hostname : h.Ip)} ({h.Ip})";
    }

    private bool Quiet(string mac)
    {
        var h = _store.GetAll().FirstOrDefault(x => string.Equals(x.Mac, mac, StringComparison.OrdinalIgnoreCase));
        return h is null || h.Ignored || h.Forgotten || _store.IsSnoozed(h.Id);
    }

    /// <summary>The new destinations and scans noted since the last check, said where they should be. Returns how many alerts went.</summary>
    public async Task<int> Evaluate(CancellationToken ct, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        List<(string Mac, string Net, string Sample, DateTime At)> dests;
        List<(string Mac, string What, int Count, string Target, DateTime At)> scans;
        var own = OwnMacs();
        lock (_lock)
        {
            dests = _newDest.ToList(); _newDest.Clear();
            scans = _scans.Where(s => !own.Contains(s.Mac)).ToList(); _scans.Clear();
            _scanLog.AddRange(scans.Select(s => new ScanSeen(s.Mac, s.What, s.Count, s.Target, s.At)));
            _scanLog.RemoveAll(s => now - s.At > TimeSpan.FromHours(2));
        }
        var sent = 0;
        if (Enabled && _scanner is not null)
        {
            foreach (var s in scans)
            {
                if (Quiet(s.Mac)) continue;
                var who = NameOf(s.Mac);
                var (title, detail) = s.What == "addresses"
                    ? ($"{who} is scanning the network", $"{who} reached out to {s.Count} different addresses on the local network inside a minute. BAMF does that when it scans, and so does malware looking for a way to spread, or a guest being nosy. If this device shouldn't be probing the network, look into it.")
                    : ($"{who} is probing ports on {s.Target}", $"{who} tried {s.Count} different ports on {s.Target} inside a minute, the way a port scanner does. If this device shouldn't be probing the network, look into it.");
                await _scanner.RaiseSecurity(title, detail, ct);
                sent++;
            }
            foreach (var group in dests.GroupBy(d => d.Mac, StringComparer.OrdinalIgnoreCase))
            {
                var mac = group.Key;
                if (own.Contains(mac) || Quiet(mac)) continue;
                int known; DateTime first;
                lock (_lock) { known = _dests.TryGetValue(mac, out var n) ? n.Count : 0; first = _firstFlow.GetValueOrDefault(mac, now); }
                if (now - first < TimeSpan.FromDays(LearnDays) || known > MaxKnownForAlert) continue;     // still learning, or talks everywhere anyway
                if (_said.TryGetValue("dest:" + mac, out var at) && now - at < TimeSpan.FromDays(1)) continue;
                _said["dest:" + mac] = now;
                var first1 = group.First();
                var name = await _reverse(first1.Sample, ct);
                var more = group.Count() > 1 ? $" and {group.Count() - 1} other new place{(group.Count() > 2 ? "s" : "")}" : "";
                await _scanner.RaiseSecurity($"{NameOf(mac)} started talking to somewhere new",
                    $"{NameOf(mac)} has talked to {known - group.Count()} outside networks in {(int)(now - first).TotalDays} days of watching, and today reached {first1.Sample}{(name is null ? "" : $" ({name})")}{more}. " +
                    "A device with a fixed set of servers doesn't usually gain one: a firmware update can explain it, and so can something that has taken it over. If it's a camera, a plug or a thermostat, that's worth a look.", ct);
                sent++;
            }
        }
        return sent;
    }

    private static async Task<string?> ReverseName(string ip, CancellationToken ct)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(2));
            var e = await Dns.GetHostEntryAsync(IPAddress.Parse(ip)).WaitAsync(limit.Token);
            return string.IsNullOrEmpty(e.HostName) ? null : e.HostName;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- bandwidth spikes

    /// <summary>Whether the last day's bytes are a spike against the previous days: at least three days to compare with, five times their median, and half a gigabyte.</summary>
    public static bool IsSpike(long lastDay, IReadOnlyList<long> priorDays)
    {
        if (priorDays.Count < SpikeMinDays) return false;
        var sorted = priorDays.OrderBy(x => x).ToList();
        var median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        return lastDay >= SpikeFloorBytes && lastDay >= median * SpikeFactor;
    }

    /// <summary>Looks for devices that moved far more in the last day than usual. Once an hour; one alert per device per day.</summary>
    public async Task<int> CheckSpikes(CancellationToken ct, DateTime? nowUtc = null)
    {
        if (!SpikeEnabled || _scanner is null) return 0;
        var now = nowUtc ?? DateTime.UtcNow;
        var today = now.ToString("yyyy-MM-dd");
        var daily = _store.DailyTraffic(now.AddDays(-8));
        var last = _store.TrafficTotals(now.AddHours(-24));
        var sent = 0;
        foreach (var (mac, days) in daily)
        {
            var prior = days.Where(kv => string.CompareOrdinal(kv.Key, today) < 0).Select(kv => kv.Value).ToList();
            var lastDay = last.TryGetValue(mac, out var t) ? t.Rx + t.Tx : 0;
            if (!IsSpike(lastDay, prior)) continue;
            if (_said.TryGetValue("spike:" + mac, out var at) && now - at < TimeSpan.FromDays(1)) continue;
            if (Quiet(mac)) continue;
            _said["spike:" + mac] = now;
            var median = prior.OrderBy(x => x).ElementAt(prior.Count / 2);
            await _scanner.RaiseSecurity($"{NameOf(mac)} moved {Gb(lastDay)} in a day, far more than usual",
                $"{NameOf(mac)} moved {Gb(lastDay)} in the last 24 hours against a usual day of about {Gb(median)} (the median of the last {prior.Count} days). " +
                "A backup or an update can do this once; a camera that has started uploading constantly, or something sending your data out, does it every day. The Traffic card on Activity shows where it went.", ct);
            sent++;
        }
        return sent;
    }

    private static string Gb(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.#} GB" : $"{b / (double)(1L << 20):0} MB";

    // ---------------------------------------------------------------- a new device's first week

    /// <summary>The share of a span a device was online, from its online/offline events in it, taken as online when it was first seen.</summary>
    public static double OnlineShare(IReadOnlyList<(DateTime At, bool Online)> events, DateTime from, DateTime to)
    {
        if (to <= from) return 0;
        var online = true; var at = from; double up = 0;
        foreach (var e in events.Where(e => e.At >= from && e.At <= to).OrderBy(e => e.At))
        {
            if (online) up += (e.At - at).TotalSeconds;
            online = e.Online; at = e.At;
        }
        if (online) up += (to - at).TotalSeconds;
        return Math.Clamp(up / (to - from).TotalSeconds, 0, 1);
    }

    /// <summary>What the first-week note says.</summary>
    public static (string Title, string Detail) FirstWeekText(string name, string vendor, string guess, double onlineShare, IReadOnlyList<int> openPorts, int destinations, long bytes, bool stillUnknown)
    {
        var ports = openPorts.Count == 0 ? "no open ports" : $"open port{(openPorts.Count == 1 ? "" : "s")} {string.Join(", ", openPorts.OrderBy(p => p).Take(8))}{(openPorts.Count > 8 ? $" and {openPorts.Count - 8} more" : "")}";
        var dest = destinations switch { 0 => "didn't talk to the internet that BAMF could see", 1 => "talked to 1 outside network", _ => $"talked to {destinations} outside networks" };
        var moved = bytes > 0 ? $", moving {Gb(bytes)}" : "";
        var who = vendor is { Length: > 0 } ? $"{name}, a {vendor} device{(guess is { Length: > 0 } ? $" that looks like {guess}" : "")}" : name;
        return ($"{name}: its first week on the network",
            $"{who}, has been here a week. It was online {onlineShare * 100:0}% of the time, has {ports}, and {dest}{moved}." +
            (stillUnknown ? " It is still marked unknown: if you know what it is, mark it known so BAMF stops treating it as a stranger; if you don't, this is the moment to find out." : ""));
    }

    /// <summary>Devices that turned a week old since the last look get their note. Once per device.</summary>
    public async Task<int> CheckFirstWeek(CancellationToken ct, DateTime? nowUtc = null)
    {
        if (!FirstWeekEnabled || _scanner is null) return 0;
        var now = nowUtc ?? DateTime.UtcNow;
        var sent = 0;
        foreach (var h in _store.GetAll())
        {
            if (h.Ignored || h.Forgotten) continue;
            if (!DateTime.TryParse(h.FirstSeen, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var first)) continue;
            var age = now - first;
            // A week old, but not long past it: a device that was already old when this arrived isn't written up.
            if (age < TimeSpan.FromDays(7) || age > TimeSpan.FromDays(8)) continue;
            if (_store.GetSetting("firstWeek:" + h.Id) is not null) continue;
            _store.SetSetting("firstWeek:" + h.Id, now.ToString("o"));
            var events = _store.EventsSince(first).GetValueOrDefault(h.Id) ?? new List<(DateTime, bool)>();
            var share = OnlineShare(events, first, now);
            var ports = _store.GetPorts(h.Id).Where(p => p.Open).Select(p => p.Port).ToList();
            int dests; lock (_lock) { if (!_loaded) Load(); dests = _dests.TryGetValue(h.Mac, out var n) ? n.Count : 0; }
            var moved = _store.TrafficTotals(first).TryGetValue(h.Mac, out var t) ? t.Rx + t.Tx : 0;
            var name = h.CustomName != "" ? h.CustomName : h.Hostname is { Length: > 0 } and not "—" ? h.Hostname : h.Ip;
            var (title, detail) = FirstWeekText(name, h.Vendor, h.OsGuess, share, ports, dests, moved, !h.Known);
            await _scanner.SendGenericAlert(title, detail, "devices", ct);
            sent++;
        }
        return sent;
    }

    // ---------------------------------------------------------------- for the card

    public sealed record DeviceFlows(string Mac, int Networks, int NewThisWeek, long Bytes, string? FirstSeen, IReadOnlyList<(string Net, string Sample, long Bytes, string LastSeen)> Top);

    /// <summary>Per device: how many outside networks it has talked to, how many are new this week, and its biggest.</summary>
    public List<DeviceFlows> Summary(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var week = now.AddDays(-7).ToString("o");
        return _store.GetFlows().GroupBy(f => f.Mac, StringComparer.OrdinalIgnoreCase).Select(g => new DeviceFlows(
            g.Key, g.Count(), g.Count(f => string.CompareOrdinal(f.FirstSeen, week) > 0), g.Sum(f => f.Bytes), g.Min(f => f.FirstSeen),
            g.OrderByDescending(f => f.Bytes).Take(3).Select(f => (f.Net, f.SampleIp, f.Bytes, f.LastSeen)).ToList()))
            .OrderByDescending(d => d.Bytes).ToList();
    }

    /// <summary>Scans said in the last while, newest first, for the 3D view.</summary>
    public List<ScanSeen> RecentScans(DateTime? nowUtc = null, TimeSpan? within = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var span = within ?? TimeSpan.FromMinutes(30);
        lock (_lock) return _scanLog.Where(s => now - s.At <= span).OrderByDescending(s => s.At).ToList();
    }

    /// <summary>
    /// Outside networks any device has talked to, seen from the other end: the most recently used first, with who talked to each.
    /// Only those used in the last week; at most <paramref name="max"/>.
    /// </summary>
    public List<Destination> Destinations(DateTime? nowUtc = null, int max = 24)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var week = now.AddDays(-7).ToString("o");
        return _store.GetFlows().Where(f => string.CompareOrdinal(f.LastSeen, week) > 0).GroupBy(f => f.Net).Select(g => new Destination(
                g.Key, g.OrderByDescending(f => f.Bytes).First().SampleIp, g.Min(f => f.FirstSeen)!, g.Max(f => f.LastSeen)!, g.Sum(f => f.Bytes),
                g.Select(f => f.Mac).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(d => d.LastSeen, StringComparer.Ordinal).ThenByDescending(d => d.Bytes).Take(max).ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        var ticks = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Evaluate(ct);
                if (++ticks % 5 == 0) { Flush(); }
                if (ticks % 60 == 0) { await CheckSpikes(ct); await CheckFirstWeek(ct); _store.PruneFlows(_store.RetentionDays); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "The flow watch failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { break; }
        }
        Flush();
    }
}
