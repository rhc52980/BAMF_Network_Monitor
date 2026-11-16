using System.Net;
using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// The two features that ask the gateway rather than the gear: devices on networks BAMF can't scan itself (VLANs, a guest
/// network), and the gateway's own events (an internet connection going down or failing over, and what its intrusion
/// detection blocked). Each is off until switched on, and the events are off kind by kind until ticked.
///
/// A network "read from the gateway" has no scan: its devices are the ones the gateway lists, found in a UniFi controller's
/// client list, an OPNsense firewall's ARP table, or the ARP table of any router that answers SNMP. A device is online while the
/// gateway still lists it, and offline after it has been missing for as many reads as a scanned network allows missed scans.
/// </summary>
public sealed partial class NetworkGear
{
    /// <summary>Swapped out by tests, to stand in for the firewall.</summary>
    internal OpnSenseGear Opn { get; } = new();

    private readonly Dictionary<long, List<(string Mac, string Ip)>> _lastArp = new();
    private List<GearHost> _gatewayHosts = new();
    private readonly List<GearEvent> _pendingThreats = new();
    private static readonly TimeSpan ThreatEvery = TimeSpan.FromMinutes(10);

    /// <summary>The OPNsense firewall to read: the one Router import already has the API key for. Null when there isn't one.</summary>
    public OpnSenseGear.Settings? OpnSenseSettings()
    {
        var cfg = _config.GetSection("Bamf:RouterImport");
        if (!(cfg["Kind"] ?? "").Trim().Equals("opnsense", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(cfg["Url"])) return null;
        return new OpnSenseGear.Settings((cfg["Url"] ?? "").Trim().TrimEnd('/'), cfg["ApiKey"] ?? "", cfg["ApiSecret"] ?? "", cfg.GetValue("VerifyCertificate", false));
    }

    /// <summary>Networks the gateway lists devices on that BAMF doesn't scan and you haven't added: a /24 around each, with how many devices.</summary>
    public List<(string Network, int Devices)> Suggestions()
    {
        var scanned = _scanner.CurrentNetworks();
        var chosen = _store.GearVlanNetworkList();
        var found = new Dictionary<string, int>();
        foreach (var h in _gatewayHosts.GroupBy(h => h.Mac.ToUpperInvariant()).Select(g => g.First()))
        {
            if (!IPAddress.TryParse(h.Ip, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            if (scanned.Concat(chosen).Any(n => ScannerService.NetworkContains(n, h.Ip))) continue;
            var b = ip.GetAddressBytes();
            var net = $"{b[0]}.{b[1]}.{b[2]}.0/24";
            if (ScannerService.CheckNetwork(net, out var label) is not null) continue;     // not private
            found[label] = found.GetValueOrDefault(label) + 1;
        }
        return found.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).Take(8).Select(f => (f.Key, f.Value)).ToList();
    }

    /// <summary>How many devices each network read from the gateway has, and how many are online.</summary>
    public List<(string Network, int Devices, int Online)> VlanSummary()
    {
        var hosts = _store.GetAll().Where(h => !h.Forgotten).ToList();
        return _store.GearVlanNetworkList().Select(n => (n, hosts.Count(h => h.Subnet == n), hosts.Count(h => h.Subnet == n && h.Online))).ToList();
    }

    private async Task GatewayPass(GearSnapshot? snap, List<HostStore.SnmpConfig> snmpConfigs, bool vlans, bool threatsOn, bool wanOn, bool force, CancellationToken ct)
    {
        var listed = new List<GearHost>();
        var gateways = new List<GearDevice>();
        var threats = new List<GearEvent>();
        var hostSources = new HashSet<string>();      // sources whose list of devices was read this time
        var wanSources = new HashSet<string>();
        var threatSources = new HashSet<string>();

        // ----- a UniFi controller: the clients and gear it lists, its gateway's connections, and its intrusion events
        if (snap is not null)
        {
            if (vlans)
            {
                listed.AddRange(snap.Clients.Where(c => c.Ip != "").Select(c => new GearHost("unifi", c.Mac, c.Ip, c.Name, c.Vendor)));
                listed.AddRange(snap.Devices.Where(d => d.Ip != "").Select(d => new GearHost("unifi", d.Mac, d.Ip, d.Name, "")));
                hostSources.Add("unifi");
            }
            if (wanOn) { gateways.AddRange(snap.Devices.Where(d => d.Kind == "router")); wanSources.Add("unifi"); }
            if (threatsOn && UniFiSettings() is { } uni)
            {
                try
                {
                    var t = await Unifi.ReadThreats(uni.Settings, ct);
                    threats.AddRange(t); threatSources.Add("unifi");
                    Set("unifi:threats", "UniFi intrusion events", "ok", t.Count == 0 ? "None logged. (A controller with Threat Management off, or one that keeps them elsewhere, lists none.)" : $"{Plural(t.Count, "event")} logged");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Set("unifi:threats", "UniFi intrusion events", "error", Why(ex)); }
            }
            else Forget("unifi:threats");
        }

        // ----- an OPNsense firewall: its ARP table, its gateways and its intrusion detection alerts
        if (OpnSenseSettings() is { } opn)
        {
            var name = "OPNsense firewall at " + (Uri.TryCreate(opn.Url, UriKind.Absolute, out var ou) ? ou.Host : opn.Url);
            var said = new List<string>(); string? error = null;
            if (vlans)
            {
                try { var h = await Opn.ReadHosts(opn, ct); listed.AddRange(h); hostSources.Add("opnsense"); said.Add(Plural(h.Count, "ARP entry", "ARP entries")); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { error = Why(ex); }
            }
            if (wanOn)
            {
                try { var g = await Opn.ReadGateway(opn, ct); gateways.Add(g); wanSources.Add("opnsense"); said.Add(Plural(g.Wans?.Count ?? 0, "gateway")); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { error ??= Why(ex); }
            }
            if (threatsOn)
            {
                try { var t = await Opn.ReadThreats(opn, ct); threats.AddRange(t); threatSources.Add("opnsense"); said.Add(Plural(t.Count, "alert")); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { error ??= Why(ex); }
            }
            Set("opnsense", name, error is null ? "ok" : "error", error ?? string.Join(", ", said));
        }
        else Forget("opnsense");

        // ----- any router over SNMP: its ARP table, as of its last read
        var arpOk = false;
        if (vlans)
        {
            var live = snmpConfigs.Select(c => c.SwitchId).ToHashSet();
            var routers = _store.GetSwitches().Where(s => live.Contains(s.Id) && s.Kind == "router").ToList();
            lock (_lastArp)
                foreach (var r in routers)
                    if (_lastArp.TryGetValue(r.Id, out var arp)) { listed.AddRange(arp.Select(e => new GearHost("snmp", e.Mac, e.Ip, "", ""))); arpOk = true; }
        }

        _gatewayHosts = listed;
        if (vlans) await ApplyVlanHosts(listed, hostSources.Count > 0 || arpOk, ct);
        if (wanOn) await ApplyWan(gateways, wanSources, ct);
        if (threatsOn) await ApplyThreats(threats, threatSources, ct);
        _ = force;
    }

    private static string Why(Exception ex) => ex is InvalidOperationException or TimeoutException ? ex.Message : ex.GetBaseException().Message;

    // ------------------------------------------------------------ devices on the networks the gateway lists

    private async Task ApplyVlanHosts(List<GearHost> listed, bool anySource, CancellationToken ct)
    {
        var scanned = _scanner.CurrentNetworks();
        var nets = _store.GearVlanNetworks().Where(n => !scanned.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        if (nets.Count == 0 || !anySource) return;      // a read that got nothing isn't a reason to say every device left

        var passStart = DateTime.UtcNow;
        var before = _store.GetAll();
        // A network read for the first time is learned quietly, as a scanned one's first devices are on a new install: the alert is for what turns up after.
        var quiet = nets.Where(n => !before.Any(h => h.Subnet == n)).ToHashSet();
        var autoIgnore = _scanner.AutoIgnoreRandomEnabled;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;

        // One entry per device; where several sources know it, the one with a name.
        foreach (var g in listed.GroupBy(h => h.Mac.ToUpperInvariant()))
        {
            var h = g.OrderByDescending(x => x.Name != "").First();
            var net = nets.FirstOrDefault(n => ScannerService.NetworkContains(n, h.Ip));
            if (net is null) continue;
            var mac = g.Key;
            var vendor = h.Vendor != "" ? h.Vendor : _oui.Lookup(mac);
            var name = g.Select(x => x.Name).FirstOrDefault(n => n != "") ?? "";
            var (isNew, ignored) = _store.UpsertSeen(mac, h.Ip, name, vendor, net, autoIgnore: autoIgnore && vendor == "(randomized MAC)");
            seen.Add(mac);
            if (!isNew) continue;
            added++;
            if (!ignored && !quiet.Contains(net)) await _scanner.NotifyNewHost(mac, h.Ip, name, vendor, net, ct);
        }

        // Gone from the list for as many reads as a scanned network may miss scans: offline, and the same alerts as for any device.
        var misses = _scanner.ConfiguredOfflineMisses;
        var wentDown = _store.MarkOffline(seen, nets, misses, null);
        await _scanner.SendStatusAlerts(wentDown, up: false, ct);
        _store.SweepAddresses(passStart, nets, misses, null);
        await _scanner.SendStatusAlerts(_store.DrainRecovered(), up: true, ct);
        if (added > 0) _log.LogInformation("Networks read from the gateway: {Added} new device(s)", added);
    }

    // ------------------------------------------------------------ the gateway's own events

    private T Remembered<T>(string key) where T : new()
    {
        try { return JsonSerializer.Deserialize<T>(_store.GetSetting(key) ?? "") ?? new T(); }
        catch (JsonException) { return new T(); }
    }

    private async Task RaiseGatewayEvent(GearEvent e, CancellationToken ct) => await RaiseGatewayEvent(e.Kind, e.Title, e.Detail, e.At, ct);

    private async Task RaiseGatewayEvent(string kind, string title, string detail, string at, CancellationToken ct)
    {
        var alertKind = kind == "threat" ? "security" : "internet";
        _log.LogWarning("Gateway event: {Title}. {Detail}", title, detail);
        _store.AddAlert(alertKind, title, detail);
        _store.LogGearEvent(kind, title, at);
        await _scanner.SendGenericAlert(title, detail, alertKind, ct);
    }

    private async Task ApplyWan(List<GearDevice> gateways, HashSet<string> sourcesRead, CancellationToken ct)
    {
        if (sourcesRead.Count == 0) return;
        var before = Remembered<GatewayEvents.WanMemory>("gearWanState");
        var (next, events) = GatewayEvents.Step(before, gateways, DateTime.UtcNow);
        // A gateway whose source couldn't be read keeps what was known about it, so a failover during the outage is still noticed afterwards.
        foreach (var (key, state) in before)
            if (!sourcesRead.Contains(key[..key.IndexOf('|')]) && !next.ContainsKey(key)) next[key] = state;
        _store.SetSetting("gearWanState", JsonSerializer.Serialize(next));
        foreach (var e in events) await RaiseGatewayEvent(e, ct);
    }

    private async Task ApplyThreats(List<GearEvent> threats, HashSet<string> sourcesRead, CancellationToken ct)
    {
        foreach (var source in sourcesRead)
        {
            var mine = threats.Where(t => t.Source == source).ToList();
            var key = "gearThreatMark:" + source;
            var mark = _store.GetSetting(key);
            if (string.IsNullOrEmpty(mark))
            {
                // The first read only sets where it starts from: what the gateway logged before is history, not news.
                _store.SetSetting(key, mine.Count > 0 ? mine.Max(t => t.At) : DateTime.UtcNow.ToString("o"));
                continue;
            }
            var fresh = mine.Where(t => string.CompareOrdinal(t.At, mark) > 0).ToList();
            if (fresh.Count == 0) continue;
            _store.SetSetting(key, fresh.Max(t => t.At));
            _pendingThreats.AddRange(fresh);
        }
        if (_pendingThreats.Count == 0) return;
        // A scan from the internet can log hundreds in a minute: one alert at most every ten minutes, saying how many.
        if (DateTime.TryParse(_store.GetSetting("gearThreatAt"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var last) && DateTime.UtcNow - last.ToUniversalTime() < ThreatEvery) return;
        var batch = _pendingThreats.ToList();
        _pendingThreats.Clear();
        _store.SetSetting("gearThreatAt", DateTime.UtcNow.ToString("o"));
        var (title, detail) = GatewayEvents.Summarize(batch);
        await RaiseGatewayEvent("threat", title, detail, DateTime.UtcNow.ToString("o"), ct);
    }

    /// <summary>Forgets where a kind of event had got to, so switching it on again starts from now rather than from a backlog.</summary>
    public void ForgetEventState(string kind)
    {
        if (kind == "threats")
        {
            _store.SetSetting("gearThreatMark:unifi", ""); _store.SetSetting("gearThreatMark:opnsense", "");
            _pendingThreats.Clear();
        }
        else if (kind == "wan") _store.SetSetting("gearWanState", "");
        else if (kind == "all") { ForgetEventState("threats"); ForgetEventState("wan"); _store.SetSetting("gearEventLog", "[]"); }
    }
}
