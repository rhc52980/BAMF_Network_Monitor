using System.Net;
using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// The ARP watch. Two devices answering for one address in the same scan is
/// an IP conflict, or one of them impersonating the other. The gateway's MAC
/// changing is either a new router or something pretending to be it. With the
/// traffic monitor running, an ARP frame that claims the gateway's address
/// from any other MAC is caught between scans too: the classic sign of ARP
/// spoofing, where a device on your network puts itself between everyone and
/// the router.
///
/// Conflicts can only be seen with active ARP: the operating system's ARP
/// table, which the ping sweep reads, keeps one MAC per address. The gateway
/// check works either way. On unless switched off in Settings.
/// </summary>
public partial class ScannerService
{
    /// <summary>A network's gateway as last seen: its address and the MAC that answered for it.</summary>
    public sealed record GatewayMac(string Ip, string Mac, string Since);

    private readonly object _arpLock = new();
    private Dictionary<string, GatewayMac>? _gatewayMacs;
    private readonly Dictionary<string, DateTime> _securityAlerted = new();
    private bool _arpWatchCached = true;
    private DateTime _arpWatchReadAt = DateTime.MinValue;

    public bool ArpWatchEnabled => _store.GetSetting("arpWatch") != "false";

    /// <summary>The same, read from the database at most once a minute: the traffic monitor asks for every ARP frame.</summary>
    private bool ArpWatchCached
    {
        get
        {
            if ((DateTime.UtcNow - _arpWatchReadAt).TotalSeconds > 60)
            {
                _arpWatchCached = ArpWatchEnabled;
                _arpWatchReadAt = DateTime.UtcNow;
            }
            return _arpWatchCached;
        }
    }

    public void ForgetArpWatchCache() => _arpWatchReadAt = DateTime.MinValue;

    private Dictionary<string, GatewayMac> GatewayMacs
    {
        get
        {
            if (_gatewayMacs is null)
            {
                try { _gatewayMacs = JsonSerializer.Deserialize<Dictionary<string, GatewayMac>>(_store.GetSetting("gatewayMacs") ?? "{}"); }
                catch (JsonException) { }
                _gatewayMacs ??= new(StringComparer.OrdinalIgnoreCase);
                _gatewayMacs = new Dictionary<string, GatewayMac>(_gatewayMacs, StringComparer.OrdinalIgnoreCase);
            }
            return _gatewayMacs;
        }
    }

    public Dictionary<string, GatewayMac> GatewayMacSnapshot()
    {
        lock (_arpLock) return new Dictionary<string, GatewayMac>(GatewayMacs, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The gateway's address on a network: the one the user declared, else the routing table's.</summary>
    private string? GatewayIpFor(string subnet)
    {
        var declared = _store.GetGateways().FirstOrDefault(g => string.Equals(g.Subnet, subnet, StringComparison.OrdinalIgnoreCase));
        if (declared is not null) return declared.Ip;
        return NetworkPlaces().TryGetValue(subnet, out var p) ? p.Gateway : null;
    }

    /// <summary>A MAC as a person would recognise it: its device's name, or its vendor.</summary>
    private string DescribeMac(string mac)
    {
        var h = _store.GetAll().FirstOrDefault(x => string.Equals(x.Mac, mac, StringComparison.OrdinalIgnoreCase));
        var name = h is null ? "" : h.CustomName != "" ? h.CustomName : h.Hostname;
        var vendor = (h?.Vendor is { Length: > 0 } v ? v : _oui.Lookup(mac)).Trim().Trim('(', ')');
        var bits = new[] { name, vendor }.Where(s => !string.IsNullOrWhiteSpace(s) && s != "—").Distinct().ToList();
        return bits.Count > 0 ? $"{mac} ({string.Join(", ", bits)})" : mac;
    }

    /// <summary>True the first time a key comes up in a window, so one problem is one alert, not one per scan.</summary>
    private bool SecurityDue(string key, TimeSpan window)
    {
        lock (_arpLock)
        {
            if (_securityAlerted.TryGetValue(key, out var at) && DateTime.UtcNow - at < window) return false;
            _securityAlerted[key] = DateTime.UtcNow;
            return true;
        }
    }

    public async Task RaiseSecurity(string title, string detail, CancellationToken ct)
    {
        _log.LogWarning("Security: {Title}. {Detail}", title, detail);
        _store.AddAlert("security", title, detail);
        await SendGenericAlert(title, detail, "security", ct);
    }

    /// <summary>Looks over one network's scan for two devices on one address, and for the gateway's MAC changing.</summary>
    private async Task CheckArp(string subnet, List<(IPAddress Ip, string Mac)> entries, CancellationToken ct)
    {
        if (!ArpWatchEnabled) return;
        var gw = GatewayIpFor(subnet);

        foreach (var g in entries.GroupBy(e => e.Ip.ToString()))
        {
            var macs = g.Select(e => e.Mac.ToUpperInvariant()).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();
            if (macs.Count < 2) continue;
            if (!SecurityDue($"conflict|{g.Key}|{string.Join(",", macs)}", TimeSpan.FromHours(24))) continue;
            var who = string.Join(" and ", macs.Select(DescribeMac));
            if (g.Key == gw)
                await RaiseSecurity($"Possible ARP spoofing: {macs.Count} devices claim to be the gateway {g.Key}",
                    $"On {subnet}, {who} all answered for the gateway's address in the same scan. One of them is your router. " +
                    "Any other is either misconfigured or pretending to be the router so your traffic goes through it.", ct);
            else
                await RaiseSecurity($"IP conflict: {macs.Count} devices answer at {g.Key}",
                    $"On {subnet}, {who} both answered for {g.Key} in the same scan. Two devices on one address fight over it, " +
                    "and connections to it break at random. Usually one has a static address inside the range DHCP hands out.", ct);
        }

        if (gw is null) return;
        var gwMacs = entries.Where(e => e.Ip.ToString() == gw).Select(e => e.Mac.ToUpperInvariant()).Distinct().ToList();
        if (gwMacs.Count != 1) return;   // not seen this scan, or already reported as a conflict above
        var mac = gwMacs[0];
        GatewayMac? prev;
        lock (_arpLock)
        {
            GatewayMacs.TryGetValue(subnet, out prev);
            if (prev is not null && prev.Ip == gw && prev.Mac == mac) return;
            GatewayMacs[subnet] = new GatewayMac(gw, mac, DateTime.UtcNow.ToString("o"));
            _store.SetSetting("gatewayMacs", JsonSerializer.Serialize(GatewayMacs));
        }
        if (prev is null || prev.Ip != gw) return;   // first sight of this gateway: just remember it
        await RaiseSecurity($"Gateway MAC changed on {subnet}",
            $"The gateway {gw} answered from {DescribeMac(mac)}, not {DescribeMac(prev.Mac)} as before. " +
            "If you replaced the router or its network card, that's expected. If not, something else may be answering for your gateway.", ct);
    }

    /// <summary>
    /// Every ARP frame the traffic monitor sees. Only a frame that claims a
    /// gateway's address from a different MAC matters; the rest cost two
    /// lookups.
    /// </summary>
    private void OnArpSeen(string senderIp, string senderMac)
    {
        if (!ArpWatchCached) return;
        string? subnet = null;
        GatewayMac? known = null;
        lock (_arpLock)
        {
            foreach (var (s, g) in GatewayMacs)
                if (g.Ip == senderIp) { subnet = s; known = g; break; }
        }
        if (known is null || string.Equals(known.Mac, senderMac, StringComparison.OrdinalIgnoreCase)) return;
        if (!SecurityDue($"spoof|{senderIp}|{senderMac.ToUpperInvariant()}", TimeSpan.FromHours(6))) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await RaiseSecurity($"Possible ARP spoofing on {subnet}: another device claims the gateway {senderIp}",
                    $"{DescribeMac(senderMac)} sent ARP for the gateway's address, which belongs to {DescribeMac(known.Mac)}. " +
                    "A device doing this is putting itself between the rest of the network and the router. If you just replaced the router, the next scan will learn its new MAC.",
                    CancellationToken.None);
            }
            catch (Exception ex) { _log.LogWarning(ex, "ARP watch alert failed"); }
        });
    }
}
