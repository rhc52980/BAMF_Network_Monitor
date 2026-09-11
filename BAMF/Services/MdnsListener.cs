using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace LanWatch.Services;

/// <summary>
/// Receive-only mDNS listener. Devices announce their own names and services
/// over multicast - Apple TVs, Chromecasts, Sonos, printers, HomeKit gear,
/// NAS boxes - and a name learned that way is usually the one a person would
/// use ("Living Room Apple TV") rather than a DHCP hostname or nothing at all.
///
/// BAMF never sends an mDNS query. It joins the group on each local IPv4
/// interface and reads what devices volunteer, which keeps the passive rule
/// intact: the only packet this causes is the kernel's IGMP membership
/// report, which any multicast receiver has to make. Announcements are
/// attributed to the address they arrived from - a device announces its own
/// services from its own address - with A records in the same packet used to
/// refine that.
///
/// Port 5353 is shared with the OS's own responder (Windows) or Avahi
/// (Linux), so the socket binds with address reuse. If the bind still fails
/// the listener records why and stays off; the scanner never depends on it.
/// </summary>
public sealed class MdnsListener : IDisposable
{
    public sealed record Observation(string Ip, string Name, IReadOnlyCollection<string> Services);

    private sealed class Entry
    {
        public string Name = "";
        public int NameRank;              // 3 friendly (TXT fn=), 2 service instance, 1 host name
        public readonly HashSet<string> Services = new(StringComparer.OrdinalIgnoreCase);
        public bool Dirty;
    }

    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private const int Port = 5353;

    private readonly ILogger<MdnsListener> _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _byIp = new();

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public bool Running { get; private set; }
    public IReadOnlyList<string> Interfaces { get; private set; } = Array.Empty<string>();
    public string? LastError { get; private set; }
    public long PacketsSeen { get; private set; }

    public MdnsListener(ILogger<MdnsListener> log) { _log = log; }

    /// <summary>Start or stop to match the setting; safe to call every pass.</summary>
    public void EnsureRunning(bool wanted)
    {
        if (wanted && !Running) Start();
        else if (!wanted && Running) Stop();
    }

    private void Start()
    {
        try
        {
            var udp = new UdpClient();
            udp.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));

            var joined = new List<string>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (!nic.SupportsMulticast) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    try
                    {
                        udp.JoinMulticastGroup(Group, ua.Address);
                        joined.Add(ua.Address.ToString());
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "mDNS: could not join the group on {Ip}", ua.Address);
                    }
                }
            }
            if (joined.Count == 0)
            {
                udp.Dispose();
                LastError = "no interface accepted the multicast join";
                _log.LogWarning("mDNS listening is on but {Why}; names from mDNS will not be learned.", LastError);
                return;
            }

            _udp = udp;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => ReceiveLoop(udp, _cts.Token));
            Interfaces = joined;
            LastError = null;
            Running = true;
            _log.LogInformation("mDNS listening on {Count} interface(s): {Ips}", joined.Count, string.Join(", ", joined));
        }
        catch (SocketException ex)
        {
            LastError = $"could not bind UDP {Port} ({ex.SocketErrorCode})";
            _log.LogWarning("mDNS listening is on but BAMF {Why}. Another mDNS responder may hold the port exclusively; " +
                            "names from mDNS will not be learned.", LastError);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.LogWarning(ex, "mDNS listener failed to start; names from mDNS will not be learned.");
        }
    }

    private void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _udp = null; _cts = null; _loop = null;
        Interfaces = Array.Empty<string>();
        Running = false;
        _log.LogInformation("mDNS listening stopped");
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception)
            {
                // A transient receive error (ICMP unreachable bounced back to a
                // UDP socket is the classic on Windows) shouldn't end listening.
                try { await Task.Delay(200, ct); } catch { break; }
                continue;
            }
            try { Handle(r.Buffer, r.RemoteEndPoint); }
            catch { /* a malformed packet is not our problem */ }
        }
    }

    /// <summary>
    /// Everything learned since the last drain, one entry per address. Entries
    /// are kept so a later packet can add to them; only the changed ones come
    /// back here.
    /// </summary>
    public List<Observation> Drain()
    {
        var list = new List<Observation>();
        lock (_lock)
        {
            foreach (var (ip, e) in _byIp)
            {
                if (!e.Dirty) continue;
                e.Dirty = false;
                list.Add(new Observation(ip, e.Name, e.Services.ToArray()));
            }
        }
        return list;
    }

    // ---------------------------------------------------------------- parsing

    private void Handle(byte[] d, IPEndPoint from)
    {
        if (d.Length < 12) return;
        if (from.AddressFamily != AddressFamily.InterNetwork) return;
        var flags = (d[2] << 8) | d[3];
        if ((flags & 0x8000) == 0) return;               // a query, not an announcement
        int qd = (d[4] << 8) | d[5], an = (d[6] << 8) | d[7], ns = (d[8] << 8) | d[9], ar = (d[10] << 8) | d[11];
        PacketsSeen++;

        var pos = 12;
        for (var i = 0; i < qd; i++)
        {
            if (!ReadName(d, ref pos, out _)) return;
            pos += 4;
        }

        string hostName = "";      // from an A record, without .local
        string instanceName = "";  // from a PTR/SRV instance, e.g. "Living Room"
        string friendlyName = "";  // from TXT fn=
        var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var total = an + ns + ar;
        for (var i = 0; i < total; i++)
        {
            if (!ReadName(d, ref pos, out var name)) return;
            if (pos + 10 > d.Length) return;
            var type = (d[pos] << 8) | d[pos + 1];
            var rdlen = (d[pos + 8] << 8) | d[pos + 9];
            pos += 10;
            if (pos + rdlen > d.Length) return;
            var rdStart = pos;

            switch (type)
            {
                case 1: // A
                    if (rdlen == 4)
                    {
                        var ip = new IPAddress(new ReadOnlySpan<byte>(d, rdStart, 4));
                        if (ip.Equals(from.Address) && hostName == "")
                            hostName = StripLocal(name);
                    }
                    break;
                case 12: // PTR
                {
                    var p = rdStart;
                    if (ReadName(d, ref p, out var target) && IsServiceType(name))
                    {
                        services.Add(StripLocal(name));
                        var inst = InstanceLabel(target, name);
                        if (inst != "" && instanceName == "") instanceName = inst;
                    }
                    break;
                }
                case 33: // SRV - the instance name is the record's own name
                {
                    var svcType = ServiceTypeOf(name);
                    if (svcType != "")
                    {
                        services.Add(svcType);
                        var inst = InstanceLabel(name, svcType + ".local");
                        if (inst != "" && instanceName == "") instanceName = inst;
                    }
                    break;
                }
                case 16: // TXT - Chromecast and friends publish a friendly name as fn=
                {
                    var p = rdStart;
                    while (p < rdStart + rdlen)
                    {
                        var len = d[p++];
                        if (len == 0 || p + len > d.Length) break;
                        var s = Encoding.UTF8.GetString(d, p, len);
                        p += len;
                        if (s.StartsWith("fn=", StringComparison.OrdinalIgnoreCase) && s.Length > 3 && friendlyName == "")
                            friendlyName = s[3..].Trim();
                    }
                    break;
                }
            }
            pos = rdStart + rdlen;
        }

        if (services.Count == 0 && hostName == "" && instanceName == "" && friendlyName == "") return;

        string best = ""; var rank = 0;
        if (Usable(friendlyName)) { best = friendlyName; rank = 3; }
        else if (Usable(instanceName)) { best = instanceName; rank = 2; }
        else if (Usable(hostName)) { best = hostName; rank = 1; }

        var key = from.Address.ToString();
        lock (_lock)
        {
            if (!_byIp.TryGetValue(key, out var e)) _byIp[key] = e = new Entry();
            var before = e.Services.Count;
            e.Services.UnionWith(services);
            if (e.Services.Count != before) e.Dirty = true;
            if (best != "" && rank >= e.NameRank && best != e.Name)
            {
                e.Name = best; e.NameRank = rank; e.Dirty = true;
            }
        }
    }

    private static bool Usable(string s) =>
        s.Length is > 0 and <= 80 && !NetBiosResolver.LooksMacDerived(s);

    private static string StripLocal(string name) =>
        name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;

    /// <summary>"_airplay._tcp.local" is a service type; "_services._dns-sd._udp.local" is the meta-query.</summary>
    private static bool IsServiceType(string name) =>
        (name.EndsWith("._tcp.local", StringComparison.OrdinalIgnoreCase) ||
         name.EndsWith("._udp.local", StringComparison.OrdinalIgnoreCase)) &&
        name.StartsWith("_", StringComparison.Ordinal) &&
        !name.StartsWith("_services._dns-sd", StringComparison.OrdinalIgnoreCase);

    /// <summary>For "Living Room._airplay._tcp.local" returns "_airplay._tcp"; "" if it isn't an instance name.</summary>
    private static string ServiceTypeOf(string instance)
    {
        var idx = instance.IndexOf("._", StringComparison.Ordinal);
        if (idx <= 0) return "";
        var rest = instance[(idx + 1)..];
        return IsServiceType(rest) ? StripLocal(rest) : "";
    }

    /// <summary>The human part of "Living Room._airplay._tcp.local" given its service type name.</summary>
    private static string InstanceLabel(string instance, string typeWithLocal)
    {
        if (!instance.EndsWith("." + typeWithLocal, StringComparison.OrdinalIgnoreCase)) return "";
        var label = instance[..^(typeWithLocal.Length + 1)];
        label = Unescape(label);
        // AirPlay audio (RAOP) instances are "MAC@Name"; keep the name.
        var at = label.IndexOf('@');
        if (at >= 0 && at < label.Length - 1 && label[..at].Replace(":", "").Length == 12) label = label[(at + 1)..];
        return label.Trim();
    }

    /// <summary>DNS-SD escapes bytes in labels as \DDD (a space is \032).</summary>
    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 3 < s.Length
                && char.IsDigit(s[i + 1]) && char.IsDigit(s[i + 2]) && char.IsDigit(s[i + 3]))
            {
                sb.Append((char)int.Parse(s.AsSpan(i + 1, 3)));
                i += 3;
            }
            else if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i++; }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Reads a DNS name at pos, following compression pointers; advances pos past it.</summary>
    private static bool ReadName(byte[] d, ref int pos, out string name)
    {
        var sb = new StringBuilder();
        var p = pos; var jumped = false; var guard = 0;
        while (true)
        {
            if (p >= d.Length || ++guard > 128) { name = ""; return false; }
            var len = d[p];
            if (len == 0) { p++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (p + 1 >= d.Length) { name = ""; return false; }
                var ptr = ((len & 0x3F) << 8) | d[p + 1];
                if (!jumped) pos = p + 2;
                jumped = true;
                p = ptr;
                continue;
            }
            p++;
            if (p + len > d.Length) { name = ""; return false; }
            if (sb.Length > 0) sb.Append('.');
            // Keep escapes intact here; InstanceLabel unescapes only the human label.
            for (var i = 0; i < len; i++)
            {
                var b = d[p + i];
                if (b == '.') sb.Append("\\046");
                else if (b < 0x20 || b > 0x7E) sb.Append('\\').Append(((int)b).ToString("D3"));
                else sb.Append((char)b);
            }
            p += len;
        }
        if (!jumped) pos = p;
        name = sb.ToString();
        return true;
    }

    public void Dispose() { if (Running) Stop(); }
}
