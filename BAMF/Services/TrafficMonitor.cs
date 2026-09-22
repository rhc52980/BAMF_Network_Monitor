using System.Net;
using System.Text.Json;
using SharpPcap;
using SharpPcap.LibPcap;

namespace LanWatch.Services;

/// <summary>
/// Watches the wire through Npcap, receive-only: nothing is ever sent. Three
/// things come out of it.
///
/// Bytes per device. Every frame's source and destination MAC are counted,
/// so each device gets bytes in and out: a rate over the last ten seconds,
/// a five-minute strip, and totals since the monitor started. What this
/// machine can see depends on where it sits: on a switched network that is
/// its own traffic plus broadcast and multicast; on a mirrored (SPAN) switch
/// port, or an old hub, it's everything. The dashboard says which.
///
/// DHCP servers. Every DHCP offer or acknowledgement names the server that
/// sent it. A second DHCP server appearing on a network is the classic sign
/// of a rogue router or a misconfigured box, so a server not seen before is
/// an alert, once. The first ones seen while BAMF has none on record are
/// learned quietly.
///
/// DNS servers. Every DNS query names the server the device asked. A device
/// that starts asking a server it never used before is an alert, and so is a
/// server no device on the network has used before. Both classic signs of a
/// device whose settings were changed behind your back.
///
/// Known servers are remembered in the settings table, so a restart doesn't
/// re-alert on the servers you already have.
/// </summary>
public sealed class TrafficMonitor : IDisposable
{
    public sealed record Alert(string At, string Kind, string Title, string Detail);
    public sealed record Counter(long RxTotal, long TxTotal, double Rx, double Tx, IReadOnlyList<long> Strip);
    public sealed record DhcpServer(string Ip, string Mac, string LastSeen, long Offers);
    public sealed record DnsServer(string Ip, string LastSeen, int Clients, long Queries);

    private sealed class Bucket
    {
        public long RxTotal, TxTotal;
        public long RxWindow, TxWindow;               // bytes in the ten seconds now being counted
        public long RxPrev, TxPrev;                   // the last full ten seconds, which the rates read
        public long RxHour, TxHour;                   // since the last flush to the hourly table
        public readonly long[] Strip = new long[30]; // bytes in+out per ten seconds, last five minutes
        public int StripAt;
    }
    private sealed class DnsUse { public long Queries; public DateTime Last; }

    private const int WindowSeconds = 10;
    public const int StripLength = 30;

    private readonly ILogger<TrafficMonitor> _log;
    private readonly HostStore _store;
    private readonly object _lock = new();
    private readonly Dictionary<string, Bucket> _byMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Mac, DateTime Last, long Offers)> _dhcp = new();
    private readonly Dictionary<string, Dictionary<string, DnsUse>> _dnsByMac = new(StringComparer.OrdinalIgnoreCase);   // client mac -> server ip -> use
    private readonly List<Alert> _alerts = new();
    private readonly Dictionary<string, DateTime> _alertedAt = new();
    private HashSet<string> _knownDhcp = new();
    private HashSet<string> _knownDns = new();
    private Dictionary<string, HashSet<string>> _deviceDns = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LibPcapLiveDevice> _devices = new();
    private Timer? _tick;
    private DateTime _startedUtc;
    private long _framesAtCheck;
    private int _quietWindows;
    private int _windowsSinceFlush;

    public bool Running { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<string> Interfaces { get; private set; } = Array.Empty<string>();
    public DateTime? StartedUtc => Running ? _startedUtc : null;
    public long Frames { get; private set; }

    /// <summary>Set by the scanner: delivers an alert wherever alerts go.</summary>
    public Func<Alert, Task>? OnAlert { get; set; }
    /// <summary>
    /// True for a device a switch or the router is counting. Its hourly history
    /// comes from them, so what the capture saw of it isn't also written down.
    /// </summary>
    public Func<string, bool>? CountedElsewhere { get; set; }
    /// <summary>Every ARP frame's sender address and MAC, for the ARP watch.</summary>
    public Action<string, string>? OnArp { get; set; }
    /// <summary>Every IPv6 neighbour-discovery frame's sender MAC and address, for the IPv6 watch.</summary>
    public Action<string, string>? OnNdp { get; set; }

    public TrafficMonitor(ILogger<TrafficMonitor> log, HostStore store)
    {
        _log = log;
        _store = store;
        LoadKnown();
    }

    public static bool Available => ArpScanner.IsAvailable;

    /// <summary>Start or stop to match the setting, on the interfaces owning the given local addresses.</summary>
    public void EnsureRunning(bool wanted, IReadOnlyCollection<IPAddress> localIps)
    {
        if (wanted && !Running) Start(localIps);
        else if (!wanted && Running) Stop();
    }

    private void Start(IReadOnlyCollection<IPAddress> localIps)
    {
        if (!Available) { LastError = "the Npcap driver isn't installed"; return; }
        var opened = new List<string>();
        try
        {
            foreach (var shared in LibPcapLiveDeviceList.Instance)
            {
                var mine = shared.Addresses.Any(a => a.Addr?.ipAddress is { } ip && localIps.Any(l => l.Equals(ip)));
                if (!mine) continue;
                // A handle of its own. The device objects in the shared list are
                // the ones the active ARP scan opens and closes each pass, and
                // closing one of those would close this capture with it.
                var dev = new LibPcapLiveDevice(shared.Interface);
                try
                {
                    dev.Open(DeviceModes.Promiscuous, 200);
                    dev.Filter = "ip or arp or icmp6";
                    dev.OnPacketArrival += OnPacket;
                    dev.StartCapture();
                    _devices.Add(dev);
                    opened.Add(dev.Addresses.Select(a => a.Addr?.ipAddress).FirstOrDefault(ip => ip is not null && localIps.Any(l => l.Equals(ip)))?.ToString() ?? dev.Name);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Traffic monitor: could not open {Device}", dev.Description ?? dev.Name);
                    try { dev.Close(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.LogWarning(ex, "Traffic monitor failed to start");
            return;
        }
        if (opened.Count == 0)
        {
            LastError = "no capture interface matched a configured network";
            _log.LogWarning("Traffic monitor is on but {Why}; bandwidth and DHCP/DNS watching are off.", LastError);
            return;
        }
        _startedUtc = DateTime.UtcNow;
        _tick = new Timer(_ => RollWindow(), null, WindowSeconds * 1000, WindowSeconds * 1000);
        Interfaces = opened;
        LastError = null;
        Running = true;
        _log.LogInformation("Traffic monitor listening on {Count} interface(s): {Ips}", opened.Count, string.Join(", ", opened));
    }

    private void Stop()
    {
        try { _tick?.Dispose(); } catch { }
        FlushHourly();
        _tick = null;
        foreach (var dev in _devices)
        {
            try { dev.OnPacketArrival -= OnPacket; dev.StopCapture(); dev.Close(); } catch { }
        }
        _devices.Clear();
        Interfaces = Array.Empty<string>();
        Running = false;
        _log.LogInformation("Traffic monitor stopped");
    }

    public void Dispose() { if (Running) Stop(); }

    // ------------------------------------------------------------ packets

    private void OnPacket(object sender, PacketCapture e)
    {
        try { Ingest(e.GetPacket().Data); }
        catch { /* a malformed frame is not our problem */ }
    }

    /// <summary>One Ethernet frame, as captured. Public so it can be fed by hand.</summary>
    public void Ingest(byte[] d)
    {
        try
        {
            if (d.Length < 34) return;
            var src = Mac(d, 6);
            var dst = Mac(d, 0);
            var len = d.Length;
            var ethType = (d[12] << 8) | d[13];
            var off = 14;
            if (ethType == 0x8100 && d.Length >= 18) { ethType = (d[16] << 8) | d[17]; off = 18; }   // VLAN tag
            lock (_lock)
            {
                Frames++;
                var sb = Get(src); sb.TxTotal += len; sb.TxWindow += len;
                if (!IsGroup(d)) { var db = Get(dst); db.RxTotal += len; db.RxWindow += len; }
            }
            // ARP: who claims which address, for the ARP watch.
            if (ethType == 0x0806)
            {
                if (d.Length >= off + 18 && OnArp is { } arp)
                    arp(new IPAddress(new ReadOnlySpan<byte>(d, off + 14, 4)).ToString(), Mac(d, off + 8));
                return;
            }
            // IPv6 neighbour discovery (router and neighbour solicitations and
            // advertisements): the sender is on this link, so its MAC owns its address.
            if (ethType == 0x86DD)
            {
                if (d.Length >= off + 41 && d[off + 6] == 58 && d[off + 40] is >= 133 and <= 136 && OnNdp is { } ndp)
                    ndp(src, new IPAddress(new ReadOnlySpan<byte>(d, off + 8, 16)).ToString());
                return;
            }
            if (ethType != 0x0800 || d.Length < off + 20) return;
            var ihl = (d[off] & 0x0F) * 4;
            var proto = d[off + 9];
            if (proto != 17 || d.Length < off + ihl + 8) return;
            var udp = off + ihl;
            var sport = (d[udp] << 8) | d[udp + 1];
            var dport = (d[udp + 2] << 8) | d[udp + 3];
            var srcIp = new IPAddress(new ReadOnlySpan<byte>(d, off + 12, 4)).ToString();
            var dstIp = new IPAddress(new ReadOnlySpan<byte>(d, off + 16, 4)).ToString();
            var payload = udp + 8;
            if (sport == 67 && dport == 68) Dhcp(d, payload, src, srcIp);
            else if (dport == 53 && d.Length >= payload + 12 && (d[payload + 2] & 0x80) == 0) Dns(src, dstIp);
        }
        catch { /* a malformed frame is not our problem */ }
    }

    private Bucket Get(string mac)
    {
        if (!_byMac.TryGetValue(mac, out var b)) _byMac[mac] = b = new Bucket();
        return b;
    }
    private static bool IsGroup(byte[] d) => (d[0] & 1) == 1;   // broadcast or multicast destination
    private static string Mac(byte[] d, int at) =>
        $"{d[at]:X2}:{d[at + 1]:X2}:{d[at + 2]:X2}:{d[at + 3]:X2}:{d[at + 4]:X2}:{d[at + 5]:X2}";

    /// <summary>Closes the ten-second window: the rates read from it, and the strip moves on.</summary>
    public void RollWindow()
    {
        // A capture that has gone quiet for two minutes has most likely lost its
        // handle; stop, and the scanner's next pass starts it again.
        if (Frames == _framesAtCheck) { if (++_quietWindows >= 12) { _quietWindows = 0; _log.LogWarning("Traffic monitor saw no frames for two minutes; restarting the capture"); Stop(); return; } }
        else { _framesAtCheck = Frames; _quietWindows = 0; }
        lock (_lock)
        {
            foreach (var b in _byMac.Values)
            {
                b.Strip[b.StripAt] = b.RxWindow + b.TxWindow;
                b.StripAt = (b.StripAt + 1) % StripLength;
                b.RxPrev = b.RxWindow; b.TxPrev = b.TxWindow;
                b.RxHour += b.RxWindow; b.TxHour += b.TxWindow;
                b.RxWindow = 0; b.TxWindow = 0;
            }
        }
        if (++_windowsSinceFlush >= 30) { _windowsSinceFlush = 0; FlushHourly(); }
    }

    /// <summary>Bytes counted since the last flush go into the hourly history, every five minutes.</summary>
    public void FlushHourly()
    {
        var hour = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:00:00Z");
        var rows = new List<(string, string, long, long)>();
        lock (_lock)
        {
            var elsewhere = CountedElsewhere;
            foreach (var (mac, b) in _byMac)
            {
                if (b.RxHour == 0 && b.TxHour == 0) continue;
                if (elsewhere?.Invoke(mac) == true) { b.RxHour = 0; b.TxHour = 0; continue; }
                rows.Add((mac, hour, b.RxHour, b.TxHour));
                b.RxHour = 0; b.TxHour = 0;
            }
        }
        try { _store.AddTraffic(rows); } catch (Exception ex) { _log.LogDebug(ex, "Traffic history write failed"); }
    }

    // ------------------------------------------------------------ DHCP and DNS

    private void Dhcp(byte[] d, int at, string serverMac, string srcIp)
    {
        // BOOTP reply, then options after the magic cookie at +236.
        if (d.Length < at + 240 || d[at] != 2) return;
        if (d[at + 236] != 0x63 || d[at + 237] != 0x82 || d[at + 238] != 0x53 || d[at + 239] != 0x63) return;
        int type = 0; string? serverId = null;
        for (var p = at + 240; p + 1 < d.Length;)
        {
            var opt = d[p];
            if (opt == 255) break;
            if (opt == 0) { p++; continue; }
            var len = d[p + 1];
            if (p + 2 + len > d.Length) break;
            if (opt == 53 && len == 1) type = d[p + 2];
            if (opt == 54 && len == 4) serverId = new IPAddress(new ReadOnlySpan<byte>(d, p + 2, 4)).ToString();
            p += 2 + len;
        }
        if (type != 2 && type != 5) return;   // offer or ack
        var ip = serverId ?? srcIp;
        bool isNew;
        lock (_lock)
        {
            _dhcp[ip] = (serverMac, DateTime.UtcNow, (_dhcp.TryGetValue(ip, out var was) ? was.Offers : 0) + 1);
            isNew = !_knownDhcp.Contains(ip);
            if (isNew)
            {
                var learnQuietly = _knownDhcp.Count == 0;
                _knownDhcp.Add(ip);
                SaveKnown();
                if (learnQuietly) { _log.LogInformation("DHCP server on the network: {Ip} ({Mac})", ip, serverMac); isNew = false; }
            }
        }
        if (isNew)
            Raise("dhcp", $"New DHCP server: {ip}", $"{Describe(serverMac)} answered a DHCP request from {ip}. " +
                "If you didn't add a router or DHCP server, something on the network is handing out addresses that shouldn't be.", ip);
    }

    private void Dns(string clientMac, string serverIp)
    {
        string? newForDevice = null, newForNetwork = null;
        lock (_lock)
        {
            if (!_dnsByMac.TryGetValue(clientMac, out var servers)) _dnsByMac[clientMac] = servers = new();
            if (!servers.TryGetValue(serverIp, out var use)) servers[serverIp] = use = new DnsUse();
            use.Queries++; use.Last = DateTime.UtcNow;

            if (!_knownDns.Contains(serverIp))
            {
                // The first few minutes with nothing on record are a warm-up: the
                // servers in use are learned, not alerted on.
                var warmup = _knownDns.Count == 0 || (DateTime.UtcNow - _startedUtc).TotalMinutes < 3 && _deviceDns.Count == 0;
                _knownDns.Add(serverIp);
                if (!warmup) newForNetwork = serverIp;
            }
            if (!_deviceDns.TryGetValue(clientMac, out var mine)) _deviceDns[clientMac] = mine = new();
            if (!mine.Contains(serverIp))
            {
                if (mine.Count > 0 && newForNetwork is null) newForDevice = serverIp;
                mine.Add(serverIp);
                if (mine.Count > 8) mine.Remove(mine.First());
            }
            if (newForNetwork is not null || newForDevice is not null) SaveKnown();
        }
        if (newForNetwork is not null)
            Raise("dns", $"New DNS server in use: {serverIp}", $"{Describe(clientMac)} asked {serverIp}, a DNS server no device on the network had used before.", serverIp);
        else if (newForDevice is not null)
            Raise("dns", $"{Describe(clientMac)} changed DNS server", $"It now asks {serverIp}; before it used {string.Join(", ", DeviceDnsBefore(clientMac, serverIp))}.", clientMac);
    }

    private IEnumerable<string> DeviceDnsBefore(string mac, string except)
    {
        lock (_lock) return _deviceDns.TryGetValue(mac, out var s) ? s.Where(x => x != except).ToList() : new List<string>();
    }

    private string Describe(string mac)
    {
        var h = _store.GetAll().FirstOrDefault(x => string.Equals(x.Mac, mac, StringComparison.OrdinalIgnoreCase));
        if (h is null) return mac;
        var name = h.CustomName != "" ? h.CustomName : h.Hostname != "" ? h.Hostname : h.Ip;
        return $"{name} ({h.Ip}, {mac}{(h.Vendor != "" ? ", " + h.Vendor : "")})";
    }

    private void Raise(string kind, string title, string detail, string key)
    {
        lock (_lock)
        {
            // Once a day per thing, so a flapping server doesn't flood anything.
            if (_alertedAt.TryGetValue(kind + ":" + key, out var last) && (DateTime.UtcNow - last).TotalHours < 24) return;
            _alertedAt[kind + ":" + key] = DateTime.UtcNow;
            _alerts.Insert(0, new Alert(DateTime.UtcNow.ToString("o"), kind, title, detail));
            if (_alerts.Count > 50) _alerts.RemoveAt(_alerts.Count - 1);
            SaveAlerts();
        }
        _log.LogWarning("{Title}: {Detail}", title, detail);
        var cb = OnAlert;
        if (cb is not null) _ = Task.Run(async () => { try { await cb(new Alert(DateTime.UtcNow.ToString("o"), kind, title, detail)); } catch (Exception ex) { _log.LogWarning(ex, "Alert delivery failed"); } });
    }

    // ------------------------------------------------------------ readers

    /// <summary>Per MAC: totals, current rates in bytes per second, and the five-minute strip (oldest first).</summary>
    public Dictionary<string, Counter> Counters()
    {
        lock (_lock)
        {
            var map = new Dictionary<string, Counter>(StringComparer.OrdinalIgnoreCase);
            foreach (var (mac, b) in _byMac)
            {
                var strip = new long[StripLength];
                for (var i = 0; i < StripLength; i++) strip[i] = b.Strip[(b.StripAt + i) % StripLength];
                map[mac] = new Counter(b.RxTotal, b.TxTotal, (double)b.RxPrev / WindowSeconds, (double)b.TxPrev / WindowSeconds, strip);
            }
            return map;
        }
    }

    /// <summary>
    /// Counters with each device's extra network cards added into its main
    /// card, keyed by the main card's MAC. Cards is how many were added up.
    /// </summary>
    public static Dictionary<string, (Counter Counter, int Cards)> Combine(Dictionary<string, Counter> counters, Dictionary<string, string> owners)
    {
        var map = new Dictionary<string, (Counter Counter, int Cards)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mac, c) in counters)
        {
            var key = owners.TryGetValue(mac, out var main) ? main : mac;
            if (!map.TryGetValue(key, out var have)) { map[key] = (c, 1); continue; }
            var a = have.Counter;
            var strip = new long[Math.Max(a.Strip.Count, c.Strip.Count)];
            for (var i = 0; i < strip.Length; i++) strip[i] = (i < a.Strip.Count ? a.Strip[i] : 0) + (i < c.Strip.Count ? c.Strip[i] : 0);
            map[key] = (new Counter(a.RxTotal + c.RxTotal, a.TxTotal + c.TxTotal, a.Rx + c.Rx, a.Tx + c.Tx, strip), have.Cards + 1);
        }
        return map;
    }

    public List<DhcpServer> DhcpServers()
    {
        lock (_lock) return _dhcp.Select(kv => new DhcpServer(kv.Key, kv.Value.Mac, kv.Value.Last.ToString("o"), kv.Value.Offers)).OrderBy(s => s.Ip).ToList();
    }

    public List<DnsServer> DnsServers()
    {
        lock (_lock)
        {
            var byServer = new Dictionary<string, (DateTime Last, int Clients, long Queries)>();
            foreach (var servers in _dnsByMac.Values)
                foreach (var (ip, use) in servers)
                {
                    var was = byServer.TryGetValue(ip, out var w) ? w : (Last: DateTime.MinValue, Clients: 0, Queries: 0L);
                    byServer[ip] = (use.Last > was.Last ? use.Last : was.Last, was.Clients + 1, was.Queries + use.Queries);
                }
            return byServer.Select(kv => new DnsServer(kv.Key, kv.Value.Last.ToString("o"), kv.Value.Clients, kv.Value.Queries)).OrderByDescending(s => s.Clients).ToList();
        }
    }

    /// <summary>Per client MAC: the DNS servers it asks, most used first.</summary>
    public Dictionary<string, List<string>> DnsByDevice()
    {
        lock (_lock)
            return _dnsByMac.ToDictionary(kv => kv.Key, kv => kv.Value.OrderByDescending(x => x.Value.Queries).Select(x => x.Key).ToList(), StringComparer.OrdinalIgnoreCase);
    }

    public List<Alert> Alerts() { lock (_lock) return _alerts.ToList(); }

    /// <summary>Forgets a server so it's treated as new again, or trusts one so it never alerts.</summary>
    public void Trust(string kind, string ip, bool trusted)
    {
        lock (_lock)
        {
            var set = kind == "dhcp" ? _knownDhcp : _knownDns;
            if (trusted) set.Add(ip); else set.Remove(ip);
            SaveKnown();
        }
    }
    public IReadOnlyCollection<string> KnownDhcp { get { lock (_lock) return _knownDhcp.ToList(); } }
    public IReadOnlyCollection<string> KnownDns { get { lock (_lock) return _knownDns.ToList(); } }

    // ------------------------------------------------------------ persistence

    private void LoadKnown()
    {
        try
        {
            _knownDhcp = JsonSerializer.Deserialize<HashSet<string>>(_store.GetSetting("knownDhcpServers") ?? "[]") ?? new();
            _knownDns = JsonSerializer.Deserialize<HashSet<string>>(_store.GetSetting("knownDnsServers") ?? "[]") ?? new();
            _deviceDns = JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(_store.GetSetting("deviceDnsServers") ?? "{}") ?? new();
            _deviceDns = new Dictionary<string, HashSet<string>>(_deviceDns, StringComparer.OrdinalIgnoreCase);
            var alerts = JsonSerializer.Deserialize<List<Alert>>(_store.GetSetting("watchAlerts") ?? "[]") ?? new();
            _alerts.AddRange(alerts);
        }
        catch { /* a damaged setting is worth less than a working monitor */ }
    }
    private void SaveKnown()
    {
        _store.SetSetting("knownDhcpServers", JsonSerializer.Serialize(_knownDhcp));
        _store.SetSetting("knownDnsServers", JsonSerializer.Serialize(_knownDns));
        _store.SetSetting("deviceDnsServers", JsonSerializer.Serialize(_deviceDns));
    }
    private void SaveAlerts() => _store.SetSetting("watchAlerts", JsonSerializer.Serialize(_alerts));
}
