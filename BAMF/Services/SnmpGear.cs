using System.Net;

namespace LanWatch.Services;

/// <summary>
/// What a managed switch says over SNMP that doesn't depend on who made it: its bridge table (which MAC it has
/// learned on which port), its uptime, its PoE budget and what each port is running at. These are the standard
/// MIBs (BRIDGE-MIB and Q-BRIDGE-MIB, SNMPv2-MIB, POWER-ETHERNET-MIB and IF-MIB), so the same read works on a
/// Cisco, an Aruba, a MikroTik, a Netgear, a UniFi switch and the rest. Read-only, like the traffic counters.
/// </summary>
public static class SnmpGear
{
    private const string QFdbPort = "1.3.6.1.2.1.17.7.1.2.2.1.2", QFdbStatus = "1.3.6.1.2.1.17.7.1.2.2.1.3";    // dot1qTpFdbPort / Status
    private const string DFdbPort = "1.3.6.1.2.1.17.4.3.1.2", DFdbStatus = "1.3.6.1.2.1.17.4.3.1.3";            // dot1dTpFdbPort / Status
    private const string BasePortIfIndex = "1.3.6.1.2.1.17.1.4.1.2";                                            // dot1dBasePortIfIndex
    private const string IfType = "1.3.6.1.2.1.2.2.1.3", IfDescr = "1.3.6.1.2.1.2.2.1.2", IfName = "1.3.6.1.2.1.31.1.1.1.1";
    private const string IfOperStatus = "1.3.6.1.2.1.2.2.1.8", IfHighSpeed = "1.3.6.1.2.1.31.1.1.1.15";
    private const string System = "1.3.6.1.2.1.1";
    private const string PoePower = "1.3.6.1.2.1.105.1.3.1.1.2", PoeUse = "1.3.6.1.2.1.105.1.3.1.1.4";         // pethMainPsePower / ConsumptionPower, watts

    public sealed record Read(
        Dictionary<string, int> Fdb, long UptimeSeconds, string SysName, string SysDescr,
        double? PoeWatts, double? PoeBudget, List<GearPort> Ports);

    /// <summary>
    /// Reads one switch. <paramref name="ports"/> is how many ports it was recorded with, which settles how its
    /// interfaces are numbered, the same as the traffic counters do.
    /// </summary>
    public static async Task<Read> ReadSwitch(IPAddress ip, string community, int snmpPort, int ports, bool wantFdb, CancellationToken ct)
    {
        var types = await Snmp.Walk(ip, community, IfType, ct, snmpPort);
        if (types.Count == 0) throw new InvalidOperationException("The switch answered but listed no interfaces.");
        var names = await Snmp.Walk(ip, community, IfName, ct, snmpPort);
        if (names.Count == 0) names = await Snmp.Walk(ip, community, IfDescr, ct, snmpPort);
        var (portOf, _) = SwitchCounters.MapPorts(types, names, ports);

        // The bridge table: which MAC the switch has learned on which port.
        var fdb = new Dictionary<string, int>();
        if (wantFdb)
        {
            var bridgePorts = await Snmp.Walk(ip, community, BasePortIfIndex, ct, snmpPort);
            var rows = await Snmp.Walk(ip, community, QFdbPort, ct, snmpPort);
            var status = rows.Count > 0 ? await Snmp.Walk(ip, community, QFdbStatus, ct, snmpPort) : new();
            if (rows.Count == 0)
            {
                rows = await Snmp.Walk(ip, community, DFdbPort, ct, snmpPort);
                status = rows.Count > 0 ? await Snmp.Walk(ip, community, DFdbStatus, ct, snmpPort) : new();
            }
            fdb = BridgeTable(rows, status, bridgePorts, portOf);
        }

        var sys = await Snmp.Walk(ip, community, System, ct, snmpPort);
        string Text(string key) => sys.TryGetValue(key, out var v) && v.Data is string s ? s.Trim() : "";
        var uptime = sys.TryGetValue("3.0", out var t) && t.Data is ulong ticks ? (long)(ticks / 100) : 0;

        var oper = await Snmp.Walk(ip, community, IfOperStatus, ct, snmpPort);
        var speed = await Snmp.Walk(ip, community, IfHighSpeed, ct, snmpPort);
        var list = new List<GearPort>();
        foreach (var (index, port) in portOf.OrderBy(p => p.Value))
            list.Add(new GearPort(port, names.TryGetValue(index, out var n) && n.Data is string name ? name : "",
                oper.TryGetValue(index, out var o) && o.Data is long and 1,
                speed.TryGetValue(index, out var sp) && sp.Data is ulong mbps ? (int)Math.Min(mbps, int.MaxValue) : 0, 0));

        // The PoE budget and what's being drawn, summed over the switch's power groups. Not every switch has any.
        var budget = await Snmp.Walk(ip, community, PoePower, ct, snmpPort);
        var drawn = budget.Count > 0 ? await Snmp.Walk(ip, community, PoeUse, ct, snmpPort) : new();
        double? Sum(Dictionary<string, Snmp.Value> rows) => rows.Count == 0 ? null : rows.Values.Where(v => v.Data is ulong).Sum(v => (double)(ulong)v.Data!);

        return new Read(fdb, uptime, Text("5.0"), Text("1.0"), Sum(drawn), Sum(budget), list);
    }

    /// <summary>
    /// The MAC addresses a bridge table lists, each with the port it was learned on, as the ports are numbered on
    /// the Map. A row's key ends in the six bytes of the MAC (Q-BRIDGE puts a VLAN number before them). Rows for
    /// the switch's own addresses and invalid ones are left out. Ports that aren't physical are left out too.
    /// </summary>
    public static Dictionary<string, int> BridgeTable(
        Dictionary<string, Snmp.Value> rows, Dictionary<string, Snmp.Value> status,
        Dictionary<string, Snmp.Value> bridgePorts, Dictionary<string, int> portOf)
    {
        var table = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, row) in rows)
        {
            if (row.Data is not long bridgePort || bridgePort <= 0) continue;
            if (status.TryGetValue(key, out var st) && st.Data is long and (2 or 4)) continue;     // invalid, or the switch's own
            var mac = MacOf(key);
            if (mac is null) continue;
            var ifIndex = bridgePorts.TryGetValue(bridgePort.ToString(), out var bp) && bp.Data is long mapped ? mapped : bridgePort;
            if (portOf.TryGetValue(ifIndex.ToString(), out var port)) table.TryAdd(mac, port);
        }
        return table;
    }

    private static string? MacOf(string key)
    {
        var parts = key.Split('.');
        if (parts.Length < 6) return null;
        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
            if (!byte.TryParse(parts[^(6 - i)], out bytes[i])) return null;
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }
}

/// <summary>Works out, from what each switch has learned, which port every device is plugged into.</summary>
public static class FdbPlacement
{
    /// <summary>One switch's bridge table: MAC to port.</summary>
    public sealed record Table(long SwitchId, Dictionary<string, int> MacPort);

    /// <summary>
    /// A device is on the port, among every switch's ports, where its MAC was learned alongside the fewest others,
    /// because the port it's really plugged into hears only what's behind it, while a port that leads on to the
    /// rest of the network has learned nearly everything. A port that has learned another switch's or an access
    /// point's own MAC (<paramref name="infrastructure"/>) leads on to that gear, so nothing is placed there: those
    /// devices are either on another table's real port, or left unplaced. If two ports tie, the device is left
    /// alone rather than guessed at.
    /// </summary>
    public static Dictionary<string, (long SwitchId, int Port)> Decide(IReadOnlyList<Table> tables, IReadOnlySet<string> infrastructure)
    {
        var candidates = new Dictionary<string, List<(long SwitchId, int Port, int Count)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables)
        {
            var onPort = t.MacPort.GroupBy(m => m.Value).ToDictionary(g => g.Key, g => g.Select(m => m.Key).ToList());
            foreach (var (port, macs) in onPort)
            {
                if (macs.Any(infrastructure.Contains)) continue;      // leads to other gear
                foreach (var mac in macs)
                {
                    if (!candidates.TryGetValue(mac, out var list)) candidates[mac] = list = new();
                    list.Add((t.SwitchId, port, macs.Count));
                }
            }
        }
        var result = new Dictionary<string, (long, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mac, list) in candidates)
        {
            var best = list.Min(c => c.Count);
            var winners = list.Where(c => c.Count == best).Select(c => (c.SwitchId, c.Port)).Distinct().ToList();
            if (winners.Count == 1) result[mac] = winners[0];
        }
        return result;
    }
}
