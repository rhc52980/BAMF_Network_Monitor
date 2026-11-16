namespace LanWatch.Services;

// What a source of network gear (a UniFi controller, a managed switch over SNMP, and whatever comes
// after them) reports, in terms that don't belong to any one make. A source fills in what it can and
// leaves the rest empty: BAMF only uses what's there, so a source that can't say a device's Wi-Fi
// signal simply gives no Wi-Fi readings, and nothing breaks.

/// <summary>One port on a piece of gear: its number as the Map counts it, whether it has a link, and how fast.</summary>
public sealed record GearPort(int Port, string Name, bool Up, int SpeedMbps, double PoeWatts);

/// <summary>A wireless network an access point broadcasts, with the radio bands it is on.</summary>
public sealed record GearSsid(string Name, string Bands);

/// <summary>
/// A switch, router or access point, as its source describes it. Kind is "switch", "router" or "ap".
/// Mac is the key BAMF knows the device by; UplinkMac and UplinkPort say what it's plugged into
/// (the parent's MAC, and the port on the parent when that's a switch).
/// </summary>
public sealed record GearDevice(
    string Source, string Mac, string Name, string Kind, string Model, string Version, string UpgradeTo,
    long UptimeSeconds, bool Online, string Ip, int PortCount, string UplinkMac, int UplinkPort,
    double? Cpu, double? Mem, double? TempC, double? PoeWatts, double? PoeBudget,
    List<GearPort> Ports, List<GearSsid> Ssids, List<GearWan>? Wans = null);

/// <summary>One internet connection on a gateway: whether it has a link, and whether it's the one traffic is using right now.</summary>
public sealed record GearWan(string Name, bool Up, string Ip, bool Active);

/// <summary>
/// A device on the network and how it connects. A wired one names the switch (by MAC) and port; a
/// wireless one names the access point (by MAC), the SSID and how well it hears the device.
/// </summary>
public sealed record GearClient(
    string Source, string Mac, string Ip, bool Wired, string ViaMac, int Port,
    string Ssid, string Band, int Channel, int? Signal, int? Noise, int? TxMbps, int? RxMbps, int? Satisfaction, int Roams,
    string Name = "", string Vendor = "");

/// <summary>Everything one source reported in one read.</summary>
public sealed record GearSnapshot(string Source, string At, List<GearDevice> Devices, List<GearClient> Clients);

/// <summary>How strong a Wi-Fi signal is, in words. The cut-offs are the usual rule of thumb for 2.4, 5 and 6 GHz.</summary>
public static class WifiQuality
{
    public static string Label(int? dbm) => dbm switch
    {
        null => "",
        >= -55 => "excellent",
        >= -67 => "good",
        >= -75 => "fair",
        _ => "weak",
    };

    /// <summary>Whether a signal is poor enough that the device is probably struggling.</summary>
    public static bool Weak(int? dbm) => dbm is < -75;
}

/// <summary>A device a gateway or router knows about, on any network it routes: the address and MAC it holds for it, and the name it has.</summary>
public sealed record GearHost(string Source, string Mac, string Ip, string Name, string Vendor);

/// <summary>
/// Something the gateway says happened. Kind is "threat" (its intrusion detection saw something; Blocked says whether it
/// stopped it) or "wan" (an internet connection went down, came back, or traffic moved to another one). At is ISO 8601.
/// </summary>
public sealed record GearEvent(string Source, string At, string Kind, string Title, string Detail, bool Blocked = false);

/// <summary>Everything a gateway reader returned in one read. Null Hosts or Events means that wasn't asked for, or can't be given.</summary>
public sealed record GatewayRead(string Source, List<GearHost>? Hosts, List<GearEvent>? Threats, List<GearDevice>? Gateways);
