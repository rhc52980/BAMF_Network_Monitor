using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// An active look at who hands out addresses. Once a day (or as often as you say) BAMF sends a DHCP DISCOVER on each of its
/// networks and listens for a few seconds. Every server that answers is noted as a DHCP server, and what its offer says is kept:
/// the gateway and the DNS servers it tells clients to use, the mask, the domain and the lease. The Network services card on
/// Activity shows them beside what the traffic monitor has heard, and works without Npcap.
///
/// It is a question and nothing more. BAMF asks as a made-up client with a fixed address of its own, never answers an offer
/// (no REQUEST, so nothing is leased and nothing is released), and the offered address is not used. The made-up client is the
/// same each time, so a server offers it the same address and its pool doesn't fill up with ghosts. It is separate from
/// whatever address this machine has, static or leased.
///
/// Needs UDP port 68 to listen on, shared with the system's own DHCP client where there is one. Where that isn't allowed the
/// result says so instead of guessing.
/// </summary>
public sealed class DhcpProbe : BackgroundService
{
    /// <summary>The client BAMF asks as: a locally administered address, the same every time.</summary>
    public static readonly byte[] ClientMac = { 0x02, 0xBA, 0x4D, 0x44, 0x48, 0x43 };
    public const int ListenSeconds = 5;

    public sealed record Offer(string Server, string? Mac, string Offered, string? Mask, List<string> Gateways, List<string> Dns,
        string? Domain, int? LeaseSeconds, List<string> Ntp);
    public sealed record NetworkResult(string Subnet, string? SelfIp, string? Error, List<Offer> Offers);
    public sealed record Result(string At, string? Error, List<NetworkResult> Networks);

    /// <summary>One question to send, on which network.</summary>
    internal sealed record Question(string Subnet, string SelfIp, int Prefix, uint Xid, byte[] Packet);
    /// <summary>One packet heard back.</summary>
    internal sealed record Heard(string From, byte[] Data);

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly TrafficMonitor _traffic;
    private readonly ILogger<DhcpProbe> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);

    /// <summary>Asks and listens. Swapped out by tests; the real one uses sockets.</summary>
    internal Func<IReadOnlyList<Question>, CancellationToken, Task<(List<Heard> Heard, string? Error, Dictionary<string, string> SendErrors)>> Exchange { get; set; }

    /// <summary>The networks and this machine's own address on each. Swapped out by tests.</summary>
    internal Func<IReadOnlyDictionary<string, ScannerService.NetworkPlace>> Places { get; set; }

    public DhcpProbe(HostStore store, ScannerService scanner, TrafficMonitor traffic, ILogger<DhcpProbe> log)
    {
        _store = store; _scanner = scanner; _traffic = traffic; _log = log;
        Exchange = RealExchange;
        Places = () => _scanner.NetworkPlaces();
    }

    public bool Enabled => _store.GetSetting("dhcpProbe") != "false";
    public int Hours => int.TryParse(_store.GetSetting("dhcpProbeHours"), out var h) ? Math.Clamp(h, 1, 168) : 24;

    public Result? Last
    {
        get
        {
            try { return JsonSerializer.Deserialize<Result>(_store.GetSetting("dhcpProbeResult") ?? "null"); }
            catch (JsonException) { return null; }
        }
    }

    public DateTime? NextDue()
    {
        if (!Enabled) return null;
        var last = Last;
        if (last is null || !DateTime.TryParse(last.At, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)) return DateTime.UtcNow;
        return at.ToUniversalTime().AddHours(Hours);
    }

    // ------------------------------------------------------------ the packets

    /// <summary>A DHCP DISCOVER from the made-up client, asking for the options worth reading. Broadcast flag set, so the answer comes as a broadcast
    /// that this machine's network card will take though the address offered isn't its own.</summary>
    public static byte[] BuildDiscover(uint xid, byte[]? mac = null)
    {
        mac ??= ClientMac;
        var p = new byte[300];
        p[0] = 1; p[1] = 1; p[2] = 6; p[3] = 0;                        // BOOTREQUEST, ethernet, six-byte address
        p[4] = (byte)(xid >> 24); p[5] = (byte)(xid >> 16); p[6] = (byte)(xid >> 8); p[7] = (byte)xid;
        p[10] = 0x80;                                                 // the broadcast flag
        Array.Copy(mac, 0, p, 28, 6);
        p[236] = 0x63; p[237] = 0x82; p[238] = 0x53; p[239] = 0x63;   // the magic cookie
        var o = 240;
        void Opt(byte code, params byte[] v) { p[o++] = code; p[o++] = (byte)v.Length; foreach (var b in v) p[o++] = b; }
        Opt(53, 1);                                                   // DISCOVER
        Opt(61, [1, .. mac]);                                         // client identifier
        var name = "bamf-probe"u8.ToArray();
        Opt(12, name);                                                // host name
        Opt(55, 1, 3, 6, 15, 28, 42, 51, 54);                         // mask, router, DNS, domain, broadcast, NTP, lease, server id
        p[o++] = 255;
        return p;
    }

    public sealed record Reply(uint Xid, int Type, string Offered, string? Server, string? Mask, List<string> Gateways, List<string> Dns,
        string? Domain, int? LeaseSeconds, List<string> Ntp, byte[] Chaddr);

    /// <summary>Reads a DHCP reply; null when it isn't one.</summary>
    public static Reply? Parse(byte[] d, int len)
    {
        if (len < 240 || d[0] != 2 || d[236] != 0x63 || d[237] != 0x82 || d[238] != 0x53 || d[239] != 0x63) return null;
        var xid = ((uint)d[4] << 24) | ((uint)d[5] << 16) | ((uint)d[6] << 8) | d[7];
        var yi = new IPAddress(new ReadOnlySpan<byte>(d, 16, 4)).ToString();
        var chaddr = d[28..34];
        int type = 0; string? server = null, mask = null, domain = null; int? lease = null;
        List<string> gws = new(), dns = new(), ntp = new();
        List<string> Ips(int at, int n) { var l = new List<string>(); for (var i = 0; i + 4 <= n; i += 4) l.Add(new IPAddress(new ReadOnlySpan<byte>(d, at + i, 4)).ToString()); return l; }
        for (var p = 240; p + 1 < len;)
        {
            var opt = d[p];
            if (opt == 255) break;
            if (opt == 0) { p++; continue; }
            var n = d[p + 1];
            if (p + 2 + n > len) break;
            var at = p + 2;
            switch (opt)
            {
                case 53 when n == 1: type = d[at]; break;
                case 54 when n == 4: server = new IPAddress(new ReadOnlySpan<byte>(d, at, 4)).ToString(); break;
                case 1 when n == 4: mask = new IPAddress(new ReadOnlySpan<byte>(d, at, 4)).ToString(); break;
                case 3: gws = Ips(at, n); break;
                case 6: dns = Ips(at, n); break;
                case 42: ntp = Ips(at, n); break;
                case 15: domain = System.Text.Encoding.ASCII.GetString(d, at, n).TrimEnd('\0'); break;
                case 51 when n == 4: lease = (int)(((uint)d[at] << 24) | ((uint)d[at + 1] << 16) | ((uint)d[at + 2] << 8) | d[at + 3]); break;
            }
            p += 2 + n;
        }
        return new Reply(xid, type, yi, server, mask, gws, dns, domain, lease, ntp, chaddr);
    }

    /// <summary>The address a broadcast on a network goes to: the network address with every host bit set.</summary>
    public static IPAddress DirectedBroadcast(IPAddress selfIp, int prefix)
    {
        var b = selfIp.GetAddressBytes();
        var mask = prefix <= 0 ? 0u : uint.MaxValue << (32 - Math.Clamp(prefix, 0, 32));
        var v = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        v |= ~mask;
        return new IPAddress(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
    }

    // ------------------------------------------------------------ the sockets

    internal async Task<(List<Heard>, string?, Dictionary<string, string>)> RealExchange(IReadOnlyList<Question> qs, CancellationToken ct)
    {
        var heard = new List<Heard>();
        var sendErrors = new Dictionary<string, string>();
        Socket? listen = null;
        try
        {
            listen = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            listen.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listen.EnableBroadcast = true;
            listen.Bind(new IPEndPoint(IPAddress.Any, 68));
        }
        catch (SocketException ex)
        {
            listen?.Dispose();
            return (heard, $"BAMF couldn't listen on UDP port 68 ({ex.SocketErrorCode}). It is in use, or needs more rights than BAMF has.", sendErrors);
        }
        using (listen)
        {
            foreach (var q in qs)
            {
                try
                {
                    using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    s.EnableBroadcast = true;
                    s.Bind(new IPEndPoint(IPAddress.Parse(q.SelfIp), 0));
                    s.SendTo(q.Packet, new IPEndPoint(IPAddress.Broadcast, 67));
                    s.SendTo(q.Packet, new IPEndPoint(DirectedBroadcast(IPAddress.Parse(q.SelfIp), q.Prefix), 67));
                }
                catch (SocketException ex) { sendErrors[q.Subnet] = $"Couldn't send ({ex.SocketErrorCode})."; }
            }
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(TimeSpan.FromSeconds(ListenSeconds));
            var buf = new byte[1500];
            try
            {
                while (!window.IsCancellationRequested)
                {
                    var r = await listen.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), window.Token);
                    heard.Add(new Heard(((IPEndPoint)r.RemoteEndPoint).Address.ToString(), buf[..r.ReceivedBytes]));
                }
            }
            catch (OperationCanceledException) { /* the listening time is up */ }
            catch (SocketException) { /* the socket closed under us */ }
        }
        return (heard, null, sendErrors);
    }

    // ------------------------------------------------------------ a round

    /// <summary>Asks on every network that has an interface here, writes down what answers, and tells the traffic monitor about the servers.
    /// Null when a round was already running.</summary>
    public async Task<Result?> RunOnce(CancellationToken ct, DateTime? nowUtc = null)
    {
        if (!await _busy.WaitAsync(0, ct)) return null;
        try
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var places = Places();
            var nets = new List<NetworkResult>();
            var asks = new List<Question>();
            var xids = new Dictionary<uint, string>();
            foreach (var (label, place) in places.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                var prefix = int.TryParse(label.Split('/').ElementAtOrDefault(1), out var pf) ? pf : 24;
                if (place.SelfIp is null || !IPAddress.TryParse(place.SelfIp, out _))
                {
                    nets.Add(new NetworkResult(label, null, "This machine has no address on that network, so it can't ask there.", new()));
                    continue;
                }
                var xid = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                xids[xid] = label;
                asks.Add(new Question(label, place.SelfIp, prefix, xid, BuildDiscover(xid)));
            }
            string? error = null;
            var offers = new Dictionary<string, Dictionary<string, Offer>>();       // subnet -> server -> offer
            var sendErrors = new Dictionary<string, string>();
            if (asks.Count > 0)
            {
                var (heard, err, sendErr) = await Exchange(asks, ct);
                error = err; sendErrors = sendErr;
                foreach (var h in heard)
                {
                    var r = Parse(h.Data, h.Data.Length);
                    if (r is null || r.Type != 2 || !xids.TryGetValue(r.Xid, out var label)) continue;
                    if (!r.Chaddr.AsSpan().SequenceEqual(ClientMac)) continue;
                    var server = r.Server ?? h.From;
                    if (!offers.TryGetValue(label, out var byServer)) offers[label] = byServer = new();
                    var mac = _store.GetAll().FirstOrDefault(x => x.Ip == server)?.Mac;
                    byServer.TryAdd(server, new Offer(server, mac, r.Offered, r.Mask, r.Gateways, r.Dns, r.Domain, r.LeaseSeconds, r.Ntp));   // the first answer of a server stands: a repeat is the same offer
                }
            }
            foreach (var q in asks)
            {
                var list = offers.TryGetValue(q.Subnet, out var o) ? o.Values.OrderBy(x => x.Server).ToList() : new List<Offer>();
                var why = error ?? (sendErrors.TryGetValue(q.Subnet, out var se) ? se : null);
                nets.Add(new NetworkResult(q.Subnet, q.SelfIp, why, list));
            }
            nets = nets.OrderBy(n => n.Subnet, StringComparer.OrdinalIgnoreCase).ToList();
            var result = new Result(now.ToString("o"), error, nets);
            _store.SetSetting("dhcpProbeResult", JsonSerializer.Serialize(result));
            foreach (var n in nets) foreach (var of in n.Offers) _traffic.NoteDhcpServer(of.Server, of.Mac, n.Subnet);
            return result;
        }
        finally { _busy.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Enabled && NextDue() is { } due && DateTime.UtcNow >= due) await RunOnce(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "The DHCP probe failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(10), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
