using System.Net;
using System.Text.RegularExpressions;

namespace LanWatch.Services;

/// <summary>
/// Real traffic per device from managed switches. A switch counts every byte
/// in and out of every port, and will say so over SNMP. You've already told
/// BAMF which port each device is plugged into for the Map, so once a minute
/// this reads each switch's counters and gives each port's bytes to the device
/// on it. No mirror port is needed: this is the switch's own count.
///
/// A port counts for a device only when exactly one device is recorded on it.
/// A port with several (an unmanaged switch or an access point beyond it) or
/// one that another recorded switch plugs into carries more than one device's
/// traffic, and BAMF can't split it, so it's left out and the status says so.
///
/// Which counter is which port: the switch's physical ports, in order, are
/// ports 1, 2, 3… unless their names end in numbers that say otherwise
/// ("gi1/0/5" is port 5), which most do.
/// </summary>
public sealed partial class SwitchCounters : BackgroundService
{
    public sealed record PortReading(int Port, string Name, string Device, long HostId, string Note);
    public sealed record Status(long SwitchId, string Name, string Address, bool Enabled, bool HasCommunity, string? LastPoll,
        string? Error, int PortsRead, string Mapping, int Counted, IReadOnlyList<PortReading> Ports);

    private const string IfType = "1.3.6.1.2.1.2.2.1.3", IfDescr = "1.3.6.1.2.1.2.2.1.2", IfName = "1.3.6.1.2.1.31.1.1.1.1";
    private const string HcIn = "1.3.6.1.2.1.31.1.1.1.6", HcOut = "1.3.6.1.2.1.31.1.1.1.10";
    private const string In32 = "1.3.6.1.2.1.2.2.1.10", Out32 = "1.3.6.1.2.1.2.2.1.16";
    /// <summary>ethernetCsmacd, fastEther, fastEtherFX, gigabitEthernet: the kinds a physical port reports.</summary>
    private static readonly HashSet<long> PhysicalTypes = new() { 6, 62, 69, 117 };

    private readonly HostStore _store;
    private readonly MeasuredTraffic _measured;
    private readonly ILogger<SwitchCounters> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly Dictionary<long, Status> _status = new();
    // switch id -> ifIndex -> (in, out, 64-bit?, when): the last reading, for the difference.
    private readonly Dictionary<long, Dictionary<string, (ulong In, ulong Out, bool Wide, DateTime At)>> _last = new();

    public SwitchCounters(HostStore store, MeasuredTraffic measured, ILogger<SwitchCounters> log)
    {
        _store = store; _measured = measured; _log = log;
    }

    public List<Status> Statuses()
    {
        var configs = _store.GetSnmpConfigs().ToDictionary(c => c.SwitchId);
        lock (_status)
            return _store.GetSwitches().Where(s => s.Kind is "switch" or "router").Select(s =>
            {
                var c = configs.GetValueOrDefault(s.Id);
                var st = _status.GetValueOrDefault(s.Id);
                return new Status(s.Id, s.Name, AddressOf(s, c) ?? "", c?.Enabled ?? false, c is not null && c.Community != "",
                    st?.LastPoll, st?.Error, st?.PortsRead ?? 0, st?.Mapping ?? "", st?.Counted ?? 0, st?.Ports ?? Array.Empty<PortReading>());
            }).ToList();
    }

    /// <summary>The address to ask: the one saved for it, else the switch's own device's.</summary>
    private string? AddressOf(SwitchRecord sw, HostStore.SnmpConfig? c)
    {
        if (c is not null && c.Address != "") return c.Address;
        return sw.HostId == 0 ? null : _store.GetAll().FirstOrDefault(h => h.Id == sw.HostId)?.Ip;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await PollAll(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogDebug(ex, "Switch counter poll failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { break; }
        }
    }

    public async Task PollAll(CancellationToken ct)
    {
        var configs = _store.GetSnmpConfigs().Where(c => c.Enabled).ToList();
        if (configs.Count == 0) return;
        foreach (var c in configs)
        {
            var sw = _store.GetSwitches().FirstOrDefault(s => s.Id == c.SwitchId);
            if (sw is null) { _store.DeleteSnmpConfig(c.SwitchId); continue; }
            await Poll(sw, c, ct);
        }
    }

    /// <summary>Reads one switch now, for the dialog's test button as well as the minute's poll.</summary>
    public async Task<Status?> PollOne(long switchId, CancellationToken ct)
    {
        var sw = _store.GetSwitches().FirstOrDefault(s => s.Id == switchId);
        var c = _store.GetSnmpConfigs().FirstOrDefault(x => x.SwitchId == switchId);
        if (sw is null || c is null) return null;
        await Poll(sw, c, ct);
        return Statuses().FirstOrDefault(s => s.SwitchId == switchId);
    }

    private async Task Poll(SwitchRecord sw, HostStore.SnmpConfig c, CancellationToken ct)
    {
        await _busy.WaitAsync(ct);
        try
        {
            var addr = AddressOf(sw, c);
            // An address, or address:port for an agent that isn't on 161.
            if (addr is null || !IPEndPoint.TryParse(addr, out var ep))
            {
                SetStatus(sw, error: "No address to ask: link the switch to its device, or type its address.");
                return;
            }
            var ip = ep.Address;
            var snmpPort = ep.Port == 0 ? 161 : ep.Port;
            var community = c.Community == "" ? "public" : c.Community;

            var types = await Snmp.Walk(ip, community, IfType, ct, snmpPort);
            if (types.Count == 0) { SetStatus(sw, error: "The switch answered but listed no interfaces."); return; }
            var names = await Snmp.Walk(ip, community, IfName, ct, snmpPort);
            if (names.Count == 0) names = await Snmp.Walk(ip, community, IfDescr, ct, snmpPort);
            var wide = true;
            var ins = await Snmp.Walk(ip, community, HcIn, ct, snmpPort);
            var outs = ins.Count > 0 ? await Snmp.Walk(ip, community, HcOut, ct, snmpPort) : new();
            if (ins.Count == 0 || outs.Count == 0)
            {
                // Older and cheaper switches only have the 32-bit counters, which
                // roll over every few minutes at gigabit; read once a minute, a
                // single rollover is still counted right.
                wide = false;
                ins = await Snmp.Walk(ip, community, In32, ct, snmpPort);
                outs = await Snmp.Walk(ip, community, Out32, ct, snmpPort);
            }

            var (portOf, mapping) = MapPorts(types, names, sw.Ports);
            var now = DateTime.UtcNow;

            // Who's on each port, as recorded. A card combined into a device counts as that device.
            var byId = _store.GetAll().Where(h => !h.Forgotten).ToDictionary(h => h.Id);
            var parentOf = _store.GetInterfaces();
            var onPort = _store.GetPlacements().Where(p => p.Value.SwitchId == sw.Id && p.Value.Port > 0)
                .GroupBy(p => p.Value.Port)
                .ToDictionary(g => g.Key, g => g.Select(p => parentOf.TryGetValue(p.Key, out var main) ? main : p.Key)
                    .Where(byId.ContainsKey).Distinct().ToList());
            var switchesBelow = _store.GetSwitches().Where(s => s.Uplink == "switch" && s.UplinkSwitch == sw.Id && s.UplinkPort > 0)
                .GroupBy(s => s.UplinkPort).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(s => s.Name)));

            if (!_last.TryGetValue(sw.Id, out var last)) _last[sw.Id] = last = new();
            var readings = new List<PortReading>();
            var counted = 0;
            foreach (var (index, port) in portOf.OrderBy(p => p.Value))
            {
                var name = names.TryGetValue(index, out var n) && n.Data is string s ? s : "";
                ulong? inNow = ins.TryGetValue(index, out var iv) && iv.Data is ulong a ? a : null;
                ulong? outNow = outs.TryGetValue(index, out var ov) && ov.Data is ulong b ? b : null;
                var devices = onPort.GetValueOrDefault(port) ?? new List<long>();
                string note, device = ""; long hostId = 0;
                if (switchesBelow.TryGetValue(port, out var below)) note = $"{below} plugs in here, so it's several devices' traffic";
                else if (devices.Count == 0) note = "nothing recorded on this port";
                else if (devices.Count > 1) note = $"{devices.Count} devices recorded here, so their traffic can't be told apart";
                else
                {
                    var h = byId[devices[0]];
                    device = h.CustomName != "" ? h.CustomName : h.Hostname != "" ? h.Hostname : h.Ip;
                    hostId = h.Id;
                    note = inNow is null || outNow is null ? "the switch gave no byte counts for this port" : "counted";
                    if (inNow is { } i && outNow is { } o)
                    {
                        if (last.TryGetValue(index, out var prev) && prev.Wide == wide)
                        {
                            var secs = (now - prev.At).TotalSeconds;
                            var dIn = Delta(prev.In, i, wide);
                            var dOut = Delta(prev.Out, o, wide);
                            // The port's "in" is what the device sent; its "out", what it got.
                            if (dIn is { } di && dOut is { } dout && secs > 1)
                                _measured.Report(h.Mac, "switch", $"{sw.Name}, port {port}", (long)dout, (long)di, secs);
                        }
                        counted++;
                    }
                }
                if (inNow is { } i2 && outNow is { } o2) last[index] = (i2, o2, wide, now);
                readings.Add(new PortReading(port, name, device, hostId, note));
            }
            SetStatus(sw, error: null, portsRead: portOf.Count, mapping: mapping, counted: counted, ports: readings);
            _log.LogDebug("Switch {Name}: {Ports} ports read, {Counted} devices counted", sw.Name, portOf.Count, counted);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            SetStatus(sw, error: ex.GetBaseException().Message);
            _log.LogInformation("Switch {Name}: SNMP read failed: {Error}", sw.Name, ex.GetBaseException().Message);
        }
        finally { _busy.Release(); }
    }

    /// <summary>
    /// How many bytes between two readings. A 32-bit counter that went down
    /// rolled over; a 64-bit one never does in practice, so going down means
    /// the switch restarted, and that minute is skipped rather than guessed.
    /// </summary>
    private static ulong? Delta(ulong before, ulong now, bool wide)
    {
        if (now >= before) return now - before;
        if (wide) return null;
        return (uint.MaxValue - before) + now + 1;
    }

    /// <summary>ifIndex -> port number, and how that was decided.</summary>
    private static (Dictionary<string, int>, string) MapPorts(Dictionary<string, Snmp.Value> types, Dictionary<string, Snmp.Value> names, int ports)
    {
        var physical = types.Where(t => t.Value.Data is long v && PhysicalTypes.Contains(v))
            .Select(t => t.Key).OrderBy(k => long.TryParse(k, out var n) ? n : long.MaxValue).ToList();
        // The last number in each name, if every port has one and no two agree.
        var numbered = physical.Select(k => (k, n: names.TryGetValue(k, out var v) && v.Data is string s && LastNumber().Match(s) is { Success: true } m ? int.Parse(m.Value) : -1)).ToList();
        var map = new Dictionary<string, int>();
        if (numbered.Count > 0 && numbered.All(x => x.n > 0) && numbered.Select(x => x.n).Distinct().Count() == numbered.Count)
        {
            foreach (var (k, n) in numbered) if (n <= ports) map[k] = n;
            return (map, "by port name");
        }
        for (var i = 0; i < physical.Count && i < ports; i++) map[physical[i]] = i + 1;
        return (map, "in order");
    }

    [GeneratedRegex(@"\d+(?=\D*$)")]
    private static partial Regex LastNumber();

    private void SetStatus(SwitchRecord sw, string? error, int portsRead = 0, string mapping = "", int counted = 0, IReadOnlyList<PortReading>? ports = null)
    {
        lock (_status)
            _status[sw.Id] = new Status(sw.Id, sw.Name, "", true, false, DateTime.UtcNow.ToString("o"), error,
                portsRead, mapping, counted, ports ?? Array.Empty<PortReading>());
    }
}
