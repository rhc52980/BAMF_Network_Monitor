using System.Net;
using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// What your own network gear can tell BAMF. Three features, each with a switch of its own and all of them off
/// until switched on:
///
///   Map        draws the switches, access points and router on the Map by itself, and puts each device on the
///              port or the Wi-Fi it's on, so you don't record the cabling by hand.
///   Wi-Fi      how well each Wi-Fi device hears its access point (signal, speed, channel), and when it moves.
///   Health     each piece of gear's state: whether it's up, its firmware, its uptime and restarts, PoE draw.
///
/// The gear is read from the sources that can say: a UniFi controller (its own API), and any managed switch
/// you've given an SNMP address (the standard bridge, PoE and interface tables, so it isn't tied to a make).
/// Wi-Fi readings and the drawn Map come from a controller; a switch's bridge table places devices on its
/// ports. A source that can't give something leaves it out, and the rest still works.
///
/// BAMF only ever reads. What it draws is marked as drawn by it; what you recorded yourself is never replaced,
/// and switching a feature off takes back what it made, keeping anything of yours that rests on it.
/// </summary>
public sealed partial class NetworkGear : BackgroundService
{
    public sealed record SourceStatus(string Id, string Name, string State, string Detail, string? At);

    public static readonly TimeSpan Every = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SnmpEvery = TimeSpan.FromSeconds(110);

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly IConfiguration _config;
    private readonly ILogger<NetworkGear> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly Dictionary<string, SourceStatus> _status = new();
    private DateTime _lastSnmp = DateTime.MinValue;
    private readonly Dictionary<long, SnmpGear.Read> _lastSnmpRead = new();

    /// <summary>The UniFi reader. Tests give it a stand-in for the controller.</summary>
    internal UniFiGear Unifi { get; } = new();

    private readonly OuiLookup _oui;

    public NetworkGear(HostStore store, ScannerService scanner, IConfiguration config, ILogger<NetworkGear> log, OuiLookup oui)
    {
        _store = store; _scanner = scanner; _config = config; _log = log; _oui = oui;
    }

    public bool AnyOn => _store.GearMapOn || _store.GearWifiOn || _store.GearHealthOn || _store.GearVlansOn || _store.GearEventsOn;

    // ------------------------------------------------------------ where the controller is

    /// <summary>
    /// The UniFi controller to read: the one saved here, else the one Router import already uses, so an install
    /// that has already given BAMF its UniFi login doesn't have to give it again. Null when there is none.
    /// </summary>
    public (UniFiGear.Settings Settings, bool FromRouterImport)? UniFiSettings()
    {
        var url = (_store.GetSetting("gearUnifiUrl") ?? "").Trim().TrimEnd('/');
        if (url != "")
            return (new UniFiGear.Settings(url, _store.GetSetting("gearUnifiUser") ?? "", _store.GetSetting("gearUnifiSecret") ?? "",
                (_store.GetSetting("gearUnifiSite") ?? "").Trim(), _store.GetSetting("gearUnifiVerify") == "true"), false);
        var cfg = _config.GetSection("Bamf:RouterImport");
        if ((cfg["Kind"] ?? "").Trim().Equals("unifi", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(cfg["Url"]))
            return (new UniFiGear.Settings((cfg["Url"] ?? "").Trim().TrimEnd('/'), cfg["Username"] ?? "", cfg["Password"] ?? "",
                (cfg["Site"] ?? "").Trim(), cfg.GetValue("VerifyCertificate", false)), true);
        return null;
    }

    public List<SourceStatus> Statuses()
    {
        lock (_status) return _status.Values.OrderBy(s => s.Id).ToList();
    }

    private void Set(string id, string name, string state, string detail)
    {
        lock (_status) _status[id] = new SourceStatus(id, name, state, detail, DateTime.UtcNow.ToString("o"));
    }

    private void Forget(string idPrefix)
    {
        lock (_status) foreach (var k in _status.Keys.Where(k => k.StartsWith(idPrefix)).ToList()) _status.Remove(k);
    }

    /// <summary>Takes back what a feature made, when it's switched off.</summary>
    public async Task TakeBack(string feature, CancellationToken ct)
    {
        await _busy.WaitAsync(ct);
        try { TakeBackNow(feature); }
        finally { _busy.Release(); }
    }

    /// <summary>Takes back everything a UniFi controller gave: what it drew, its Wi-Fi readings and its gear.</summary>
    public async Task TakeBackUniFi(CancellationToken ct)
    {
        await _busy.WaitAsync(ct);
        try { _store.ClearAutoMap("unifi"); _store.ClearWifi(); _store.ClearGear("unifi"); }
        finally { _busy.Release(); }
    }

    private void TakeBackNow(string feature)
    {
        switch (feature)
        {
            case "map": _store.ClearAutoMap(); break;
            case "wifi": _store.ClearWifi(); break;
            case "health": _store.ClearGear(); break;
            case "vlans": _store.ForgetGatewayHosts(_store.GearVlanNetworkList()); break;
            case "events": ForgetEventState("all"); break;
        }
    }

    // ------------------------------------------------------------ the loop

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { if (AnyOn) await Refresh(false, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "The network gear reader failed"); }
            try { await Task.Delay(Every, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Reads the gear and applies what it said, for the features that are on. <paramref name="force"/> reads the
    /// switches over SNMP as well, whatever their schedule. Returns false if a read was already running.
    /// </summary>
    public async Task<bool> Refresh(bool force, CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct)) return false;
        try
        {
            var map = _store.GearMapOn; var wifi = _store.GearWifiOn; var health = _store.GearHealthOn;
            var vlans = _store.GearVlansOn; var threatsOn = _store.GearEventThreats; var wanOn = _store.GearEventWan;
            var gateway = vlans || threatsOn || wanOn;
            if (!map && !wifi && !health && !gateway) return true;

            var hosts = _store.GetAll().Where(h => !h.Forgotten && h.Mac != "").ToList();
            var now = DateTime.UtcNow.ToString("o");
            var read = new HashSet<string>();

            // ----- a UniFi controller
            GearSnapshot? snap = null;
            if (UniFiSettings() is { } uni)
            {
                var name = "UniFi controller at " + (Uri.TryCreate(uni.Settings.Url, UriKind.Absolute, out var u) ? u.Host : uni.Settings.Url);
                try
                {
                    snap = await Unifi.Read(uni.Settings, ct);
                    read.Add("unifi");
                    Set("unifi", name, "ok", $"{Plural(snap.Devices.Count, "piece")} of gear and {Plural(snap.Clients.Count, "device")} read");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var why = ex is InvalidOperationException ? ex.Message : ex.GetBaseException().Message;
                    Set("unifi", name, "error", why);
                    _log.LogInformation("UniFi gear read failed: {Why}", why);
                }
            }
            else Forget("unifi");

            // ----- managed switches over SNMP: the ones whose counters are already on
            var snmpReads = new Dictionary<long, (SwitchRecord Switch, SnmpGear.Read Read)>();
            var snmpOk = true;
            var snmpConfigs = (map || health || vlans) ? _store.GetSnmpConfigs().Where(c => c.Enabled).ToList() : new();
            if (snmpConfigs.Count == 0) Forget("snmp");
            else if (force || DateTime.UtcNow - _lastSnmp >= SnmpEvery)
            {
                _lastSnmp = DateTime.UtcNow;
                var switches = _store.GetSwitches();
                foreach (var c in snmpConfigs)
                {
                    var sw = switches.FirstOrDefault(s => s.Id == c.SwitchId);
                    if (sw is null) continue;
                    var id = "snmp:" + sw.Id;
                    try
                    {
                        var addr = c.Address != "" ? c.Address : sw.HostId > 0 ? hosts.FirstOrDefault(h => h.Id == sw.HostId)?.Ip : null;
                        if (addr is null || !IPEndPoint.TryParse(addr, out var ep)) throw new InvalidOperationException("No address to ask: link the switch to its device, or type its address.");
                        var community = c.Community == "" ? "public" : c.Community;
                        var detail = new List<string>();
                        if (map || health)
                        {
                            var r = await SnmpGear.ReadSwitch(ep.Address, community, ep.Port == 0 ? 161 : ep.Port, sw.Ports, map, ct);
                            lock (_lastSnmpRead) _lastSnmpRead[sw.Id] = r;
                            snmpReads[sw.Id] = (sw, r);
                            detail.Add(map ? $"{Plural(r.Fdb.Count, "device")} on its ports, {Plural(r.Ports.Count(p => p.Up), "port")} up" : $"{Plural(r.Ports.Count(p => p.Up), "port")} up");
                        }
                        // A router's ARP table lists every device on every network it's on, VLANs included.
                        if (vlans && sw.Kind == "router")
                        {
                            var arp = await SnmpGear.ReadArp(ep.Address, community, ep.Port == 0 ? 161 : ep.Port, ct);
                            lock (_lastArp) _lastArp[sw.Id] = arp;
                            detail.Add(Plural(arp.Count, "ARP entry", "ARP entries"));
                        }
                        Set(id, $"{(sw.Kind == "router" ? "Router" : "Switch")} {sw.Name}", "ok", string.Join(", ", detail));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        snmpOk = false;
                        Set(id, $"Switch {sw.Name}", "error", ex is InvalidOperationException or TimeoutException ? ex.Message : ex.GetBaseException().Message);
                    }
                }
                if (snmpReads.Count > 0 && snmpOk) read.Add("snmp");
                var live = snmpConfigs.Select(c => "snmp:" + c.SwitchId).ToHashSet();
                lock (_status) foreach (var k in _status.Keys.Where(k => k.StartsWith("snmp:") && !live.Contains(k)).ToList()) _status.Remove(k);
            }
            else
            {
                // Between SNMP reads, what the last one said stands, so a minute with only the controller doesn't blank the switches.
                var switches = _store.GetSwitches();
                lock (_lastSnmpRead)
                    foreach (var c in snmpConfigs)
                        if (switches.FirstOrDefault(s => s.Id == c.SwitchId) is { } sw && _lastSnmpRead.TryGetValue(sw.Id, out var r)) snmpReads[sw.Id] = (sw, r);
            }

            // ----- what each feature does with it
            if (map)
            {
                if (snmpReads.Count > 0) ApplyBridgeTables(snmpReads.Values.ToList(), hosts);
                if (snap is not null) ApplyUniFiMap(snap, hosts);
            }
            if (wifi && snap is not null) ApplyWifi(snap, hosts);
            if (health) ApplyHealth(snap, snmpReads.Values.ToList(), hosts, read, now);
            if (gateway) await GatewayPass(snap, snmpConfigs, vlans, threatsOn, wanOn, ct);
            return true;
        }
        finally { _busy.Release(); }
    }

    private static string Plural(int n, string one, string many = "") => n == 1 ? $"1 {one}" : $"{n:N0} {(many == "" ? one + "s" : many)}";

    // ------------------------------------------------------------ the Map, from a controller

    private string NetworkOf(string ip, HostRecord? host)
    {
        if (host is not null && host.Subnet != "") return host.Subnet;
        var nets = _scanner.CurrentNetworks();
        return nets.FirstOrDefault(n => ip != "" && ScannerService.NetworkContains(n, ip)) ?? nets.FirstOrDefault() ?? "";
    }

    /// <summary>
    /// A gateway's LAN interface usually has a MAC of its own, not the one its controller lists, so no host matches the gear by MAC.
    /// It is the host at the address the controller says the gateway holds on a network, which is how the Map comes to draw one gateway
    /// and not two. Only for a router nothing else matched; a host is given to one router.
    /// </summary>
    internal static HostRecord? GatewayHost(GearDevice d, GearSnapshot snap, IEnumerable<HostRecord> hosts, HashSet<long> taken)
    {
        if (d.Kind != "router" || snap.Gateways is not { Count: > 0 } gws) return null;
        var host = hosts.FirstOrDefault(h => !h.Forgotten && gws.Contains(h.Ip) && !taken.Contains(h.Id)
            && !snap.Devices.Any(o => o != d && o.Mac.Equals(h.Mac, StringComparison.OrdinalIgnoreCase)));
        if (host is not null) taken.Add(host.Id);
        return host;
    }

    private void ApplyUniFiMap(GearSnapshot snap, List<HostRecord> hosts)
    {
        if (snap.Devices.Count == 0) return;     // an empty answer is not a reason to take the Map down
        var byMac = hosts.ToDictionary(h => h.Mac.ToUpperInvariant(), h => h, StringComparer.OrdinalIgnoreCase);
        var switches = _store.GetSwitches();
        var hostMac = hosts.ToDictionary(h => h.Id, h => h.Mac.ToUpperInvariant());

        // The record that is each piece of gear: yours if you made one for its device, else the one BAMF draws.
        var idOfMac = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var mine = new HashSet<long>();
        foreach (var s in switches.Where(s => s.HostId > 0 && hostMac.ContainsKey(s.HostId)))
            idOfMac[hostMac[s.HostId]] = s.Id;
        var autos = _store.GetAutoSwitches().Where(a => a.Source == "unifi").ToDictionary(a => a.Key, a => a.Id);
        foreach (var s in switches) mine.Add(s.Id);
        foreach (var id in autos.Values) mine.Remove(id);
        var kindOf = snap.Devices.ToDictionary(d => d.Mac, d => d.Kind, StringComparer.OrdinalIgnoreCase);

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ignored = _store.GetAutoIgnored();     // drawn ones you deleted: not drawn again
        var gatewayTaken = new HashSet<long>();
        foreach (var d in Parents(snap.Devices))
        {
            keys.Add(d.Mac);
            if (ignored.Contains("unifi|" + d.Mac.ToUpperInvariant())) continue;
            byMac.TryGetValue(d.Mac, out var host);
            host ??= GatewayHost(d, snap, hosts, gatewayTaken);
            // One you made for this device is left exactly as it is.
            if (idOfMac.TryGetValue(d.Mac, out var own) && mine.Contains(own)) continue;
            long parent = 0; var uplink = "";
            if (d.UplinkMac != "" && idOfMac.TryGetValue(d.UplinkMac, out parent))
                uplink = kindOf.GetValueOrDefault(d.UplinkMac) == "router" ? "router" : "switch";
            if (uplink == "router") parent = 0;
            idOfMac[d.Mac] = _store.UpsertAutoSwitch("unifi", d.Mac.ToUpperInvariant(), d.Name, d.Kind, d.Kind == "ap" ? 1 : Math.Max(1, d.PortCount),
                NetworkOf(d.Ip, host), host?.Id ?? 0, uplink, parent, d.UplinkPort, 0);
        }

        // The wireless networks each access point broadcasts, and any a device is on that the list missed.
        switches = _store.GetSwitches();
        var wanted = new List<(string ApMac, string Ssid)>();
        foreach (var d in snap.Devices.Where(d => d.Kind == "ap")) wanted.AddRange(d.Ssids.Select(s => (d.Mac, s.Name)));
        wanted.AddRange(snap.Clients.Where(c => !c.Wired && c.Ssid != "" && kindOf.GetValueOrDefault(c.ViaMac) == "ap").Select(c => (c.ViaMac, c.Ssid)));
        var ssidId = new Dictionary<(string, string), long>();
        // A guest SSID usually puts its devices on a network of its own: the one most of them are on.
        var ssidNet = snap.Clients.Where(c => !c.Wired && c.Ssid != "" && byMac.ContainsKey(c.Mac))
            .GroupBy(c => (c.ViaMac.ToUpperInvariant(), c.Ssid))
            .ToDictionary(g => g.Key, g => g.GroupBy(c => byMac[c.Mac].Subnet).Where(n => n.Key != "").OrderByDescending(n => n.Count()).Select(n => n.Key).FirstOrDefault() ?? "");
        foreach (var (apMac, ssid) in wanted.Distinct())
        {
            if (!idOfMac.TryGetValue(apMac, out var apId) || switches.FirstOrDefault(s => s.Id == apId) is not { Kind: "ap" } ap) continue;
            var existing = switches.FirstOrDefault(s => s.Kind == "ssid" && s.RunsOn == apId && s.Name.Equals(ssid, StringComparison.OrdinalIgnoreCase));
            var key = apMac.ToUpperInvariant() + "|" + ssid;
            if (existing is not null) { ssidId[(apMac.ToUpperInvariant(), ssid)] = existing.Id; if (autos.ContainsKey(key)) keys.Add(key); continue; }
            keys.Add(key);
            if (ignored.Contains("unifi|" + key)) continue;
            var net = ssidNet.GetValueOrDefault((apMac.ToUpperInvariant(), ssid)) is { Length: > 0 } n ? n : ap.Subnet;
            ssidId[(apMac.ToUpperInvariant(), ssid)] = _store.UpsertAutoSwitch("unifi", key, ssid, "ssid", 1, net, 0, "", 0, 0, apId);
        }
        _store.RemoveStaleAutoSwitches("unifi", keys);

        // Each device on the port or the Wi-Fi it's on. A card combined into another device counts as that device.
        var parentOf = _store.GetInterfaces();
        var place = new Dictionary<long, (long Switch, int Port)>();
        foreach (var direct in new[] { false, true })      // a device's own entry beats its cards'
            foreach (var c in snap.Clients)
            {
                if (!byMac.TryGetValue(c.Mac, out var h)) continue;
                var isCard = parentOf.ContainsKey(h.Id);
                if (isCard == direct) continue;
                var target = isCard ? parentOf[h.Id] : h.Id;
                if (c.Wired)
                {
                    if (idOfMac.TryGetValue(c.ViaMac, out var sw)) place[target] = (sw, c.Port);
                }
                else if (ssidId.TryGetValue((c.ViaMac.ToUpperInvariant(), c.Ssid), out var wl)) place[target] = (wl, 0);
                else if (idOfMac.TryGetValue(c.ViaMac, out var viaGear)) place[target] = (viaGear, 0);
            }
        _store.ApplyAutoPlacements("unifi", place.Select(p => (p.Key, p.Value.Switch, p.Value.Port)).ToList());
    }

    /// <summary>Gear with what it's plugged into first, so a switch is drawn before what hangs off it.</summary>
    private static IEnumerable<GearDevice> Parents(List<GearDevice> devices)
    {
        var byMac = devices.ToDictionary(d => d.Mac, StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<GearDevice>();
        void Add(GearDevice d, int depth)
        {
            if (done.Contains(d.Mac) || depth > 12) return;
            if (d.UplinkMac != "" && byMac.TryGetValue(d.UplinkMac, out var parent) && !done.Contains(parent.Mac)) Add(parent, depth + 1);
            if (done.Add(d.Mac)) order.Add(d);
        }
        foreach (var d in devices) Add(d, 0);
        return order;
    }

    // ------------------------------------------------------------ the Map, from a switch's bridge table

    private void ApplyBridgeTables(List<(SwitchRecord Switch, SnmpGear.Read Read)> reads, List<HostRecord> hosts)
    {
        var byMac = hosts.ToDictionary(h => h.Mac.ToUpperInvariant(), h => h, StringComparer.OrdinalIgnoreCase);
        var hostMac = hosts.ToDictionary(h => h.Id, h => h.Mac.ToUpperInvariant());
        // Gear and gateways: a port that has learned one of those leads on to it, and nothing is placed on that port.
        var infra = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _store.GetSwitches().Where(s => s.HostId > 0 && hostMac.ContainsKey(s.HostId))) infra.Add(hostMac[s.HostId]);
        foreach (var g in _store.GetGateways().Where(g => hostMac.ContainsKey(g.HostId))) infra.Add(hostMac[g.HostId]);

        var decided = FdbPlacement.Decide(reads.Select(r => new FdbPlacement.Table(r.Switch.Id, r.Read.Fdb)).ToList(), infra);
        var parentOf = _store.GetInterfaces();
        var place = new Dictionary<long, (long Switch, int Port)>();
        foreach (var (mac, spot) in decided)
        {
            if (!byMac.TryGetValue(mac, out var h)) continue;
            place[parentOf.TryGetValue(h.Id, out var p) ? p : h.Id] = spot;
        }
        _store.ApplyAutoPlacements("snmp", place.Select(p => (p.Key, p.Value.Switch, p.Value.Port)).ToList());
    }

    // ------------------------------------------------------------ Wi-Fi

    private void ApplyWifi(GearSnapshot snap, List<HostRecord> hosts)
    {
        var byMac = hosts.ToDictionary(h => h.Mac.ToUpperInvariant(), h => h, StringComparer.OrdinalIgnoreCase);
        var apName = snap.Devices.ToDictionary(d => d.Mac, d => d.Name, StringComparer.OrdinalIgnoreCase);
        var parentOf = _store.GetInterfaces();
        var now = snap.At;
        var links = new Dictionary<long, HostStore.WifiLink>();
        var wired = new HashSet<long>();
        foreach (var c in snap.Clients)
        {
            if (!byMac.TryGetValue(c.Mac, out var h)) continue;
            var target = parentOf.TryGetValue(h.Id, out var p) ? p : h.Id;
            if (c.Wired) { wired.Add(target); continue; }
            links[target] = new HostStore.WifiLink(target, c.ViaMac, apName.GetValueOrDefault(c.ViaMac) ?? c.ViaMac, c.Ssid, c.Band, c.Channel,
                c.Signal, c.Noise, c.TxMbps, c.RxMbps, c.Satisfaction, c.Roams, now, now, "", "");
        }
        wired.ExceptWith(links.Keys);
        _store.SaveWifi(links.Values.ToList(), wired);
    }

    // ------------------------------------------------------------ gear health

    private void ApplyHealth(GearSnapshot? snap, List<(SwitchRecord Switch, SnmpGear.Read Read)> snmp, List<HostRecord> hosts, HashSet<string> sourcesRead, string now)
    {
        var byId = hosts.ToDictionary(h => h.Id);
        var gear = new Dictionary<string, GearDevice>(StringComparer.OrdinalIgnoreCase);
        // A switch read over SNMP first, so a controller that knows the same switch says it better.
        foreach (var (sw, r) in snmp)
        {
            var host = sw.HostId > 0 && byId.TryGetValue(sw.HostId, out var hh) ? hh : null;
            var mac = host is not null ? host.Mac.ToUpperInvariant() : "snmp:" + sw.Id;
            var descr = r.SysDescr.Length > 80 ? r.SysDescr[..80] : r.SysDescr;
            gear[mac] = new GearDevice("snmp", mac, sw.Name, sw.Kind, descr, "", "", r.UptimeSeconds, true, host?.Ip ?? "", r.Ports.Count, "", 0,
                null, null, null, r.PoeWatts, r.PoeBudget is > 0 ? r.PoeBudget : null, r.Ports, new());
        }
        if (snap is not null) foreach (var d in snap.Devices) gear[d.Mac] = d;
        if (gear.Count == 0 && sourcesRead.Count == 0) return;
        _store.SaveGear(gear.Values.ToList(), sourcesRead, now);
    }

    // ------------------------------------------------------------ what's wrong, in words

    public sealed record Issue(string Level, string Text);

    /// <summary>What needs a look on one piece of gear. Level is "warn" or "info". Nothing here sends an alert: it's what the Network gear card shows.</summary>
    public static List<Issue> IssuesFor(HostStore.GearRow g, DateTime now)
    {
        var o = new List<Issue>();
        if (!g.Online) o.Add(new("warn", "The controller says it's offline."));
        if (g.UpgradeTo != "") o.Add(new("info", g.UpgradeTo == "newer" ? "Newer firmware is available." : $"Firmware {g.UpgradeTo} is available."));
        if (g.LastReboot != "" && DateTime.TryParse(g.LastReboot, null, System.Globalization.DateTimeStyles.RoundtripKind, out var rb) && now - rb < TimeSpan.FromHours(24))
            o.Add(new("warn", $"It restarted {Ago(now - rb)} ago."));
        if (g.TempC is >= 80) o.Add(new("warn", $"It's running hot: {g.TempC:0} °C."));
        if (g.PoeWatts is { } w && g.PoeBudget is > 0 && w >= g.PoeBudget * 0.9)
            o.Add(new("warn", $"PoE is nearly used up: {w:0} W of {g.PoeBudget:0} W."));
        if (g.Cpu is >= 90) o.Add(new("warn", $"CPU is at {g.Cpu:0}%."));
        if (g.Mem is >= 95) o.Add(new("warn", $"Memory is at {g.Mem:0}%."));
        // A port running at 10 or 100 on a switch whose other ports run at a gigabit is usually a bad cable or a device that only does that.
        if (g.Kind != "ap")
        {
            var up = g.Ports.Where(p => p.Up && p.SpeedMbps > 0).ToList();
            if (up.Any(p => p.SpeedMbps >= 1000))
                foreach (var p in up.Where(p => p.SpeedMbps <= 100))
                    o.Add(new("info", $"Port {p.Port}{(p.Name != "" && p.Name != $"Port {p.Port}" ? " (" + p.Name + ")" : "")} is linked at {p.SpeedMbps} Mbps."));
        }
        return o;
    }

    private static string Ago(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h" : $"{Math.Max(1, (int)t.TotalMinutes)} min";
}
