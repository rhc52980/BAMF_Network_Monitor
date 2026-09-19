using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace LanWatch.Services;

/// <summary>
/// The IPv6 watch. BAMF finds devices by ARP, which is IPv4's; a device can
/// also have IPv6 addresses, and a few talk mostly or only IPv6. Every five
/// minutes BAMF sends one ping to the all-nodes address on each network it
/// scans, which makes every IPv6 device answer, and reads this machine's
/// neighbour table: IPv6's ARP table. With the traffic monitor running,
/// neighbour discovery on the wire is picked up as it happens too. Addresses
/// are kept by MAC, next to the device's IPv4 address.
/// </summary>
public partial class ScannerService
{
    private readonly ConcurrentDictionary<(string Mac, string Ip), DateTime> _ndpSeen = new();
    private DateTime _ipv6ReadAt = DateTime.MinValue;

    public bool Ipv6WatchEnabled => _store.GetSetting("ipv6Watch") != "false";
    public DateTime? LastIpv6Read { get; private set; }
    public string? Ipv6Error { get; private set; }

    /// <summary>A neighbour-discovery frame's sender, from the traffic monitor. Buffered; written with the scan.</summary>
    private void OnNdpSeen(string mac, string ip)
    {
        if (Usable(mac, ip) is { } addr) _ndpSeen[(mac, addr)] = DateTime.UtcNow;
    }

    /// <summary>Once per scan pass: what the wire said, and every five minutes the neighbour table.</summary>
    private async Task WatchIpv6(CancellationToken ct)
    {
        if (!Ipv6WatchEnabled) { _ndpSeen.Clear(); return; }
        var seen = new List<(string Mac, string Ip, DateTime At)>();
        foreach (var key in _ndpSeen.Keys.ToList())
            if (_ndpSeen.TryRemove(key, out var at)) seen.Add((key.Mac, key.Ip, at));

        if (DateTime.UtcNow - _ipv6ReadAt >= TimeSpan.FromMinutes(5))
        {
            _ipv6ReadAt = DateTime.UtcNow;
            try
            {
                // Only the interfaces on networks BAMF scans: a second network card
                // on a network it isn't configured for stays out of it.
                var nics = ScannedInterfaces();
                await PingAllNodes(nics, ct);
                var now = DateTime.UtcNow;
                var entries = OperatingSystem.IsWindows()
                    ? await ReadNeighboursWindows(nics.Select(n => n.Index).ToHashSet(), ct)
                    : await ReadNeighboursLinux(nics.Select(n => n.Name).ToHashSet(), ct);
                foreach (var (ip, mac) in entries)
                    if (Usable(mac, ip) is { } addr) seen.Add((mac, addr, now));
                LastIpv6Read = now;
                Ipv6Error = null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Ipv6Error = ex.GetBaseException().Message;
                _log.LogInformation("IPv6 neighbour read failed: {Error}", Ipv6Error);
            }
        }
        _store.SeenIpv6(seen);
    }

    /// <summary>The interfaces that carry a network BAMF scans and have IPv6: their index, name and link-local address.</summary>
    private List<(int Index, string Name, IPAddress LinkLocal)> ScannedInterfaces()
    {
        var mine = NetworkPlaces().Values.Select(p => p.SelfIp).Where(ip => ip is not null).ToHashSet();
        var list = new List<(int, string, IPAddress)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.Supports(NetworkInterfaceComponent.IPv6)) continue;
            var props = nic.GetIPProperties();
            if (!props.UnicastAddresses.Any(u => mine.Contains(u.Address.ToString()))) continue;
            var ll = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && u.Address.IsIPv6LinkLocal);
            if (ll is null) continue;
            list.Add((props.GetIPv6Properties().Index, nic.Name, ll.Address));
        }
        return list;
    }

    /// <summary>
    /// One echo to ff02::1 on each of those interfaces: every IPv6 device on
    /// the link answers, which fills the neighbour table.
    /// </summary>
    private async Task PingAllNodes(List<(int Index, string Name, IPAddress LinkLocal)> nics, CancellationToken ct)
    {
        foreach (var (_, _, ll) in nics)
        {
            try
            {
                using var ping = new Ping();
                await ping.SendPingAsync(new IPAddress(IPAddress.Parse("ff02::1").GetAddressBytes(), ll.ScopeId), 1000);
            }
            catch (Exception ex) when (ex is PingException or SocketException or NotSupportedException) { }
            ct.ThrowIfCancellationRequested();
        }
        await Task.Delay(1500, ct);   // let the answers settle into the table
    }

    [GeneratedRegex(@"^\s*([0-9a-fA-F:]+)(?:%\d+)?\s+([0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})\s")]
    private static partial Regex Ipv6NeighbourLine();

    [GeneratedRegex(@"^\S.*?\s(\d+):\s")]
    private static partial Regex Ipv6InterfaceHeader();

    /// <summary>
    /// Windows: netsh's neighbour list, a section per interface headed
    /// "Interface 13: Ethernet". The index, addresses and MACs parse the same
    /// in any language.
    /// </summary>
    private static async Task<List<(string Ip, string Mac)>> ReadNeighboursWindows(HashSet<int> interfaces, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("netsh", "interface ipv6 show neighbors")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("netsh didn't start");
        var output = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var list = new List<(string, string)>();
        var keep = false;
        foreach (var line in output.Split('\n'))
        {
            if (Ipv6InterfaceHeader().Match(line) is { Success: true } head) { keep = interfaces.Contains(int.Parse(head.Groups[1].Value)); continue; }
            if (keep && Ipv6NeighbourLine().Match(line) is { Success: true } m)
                list.Add((m.Groups[1].Value, m.Groups[2].Value.Replace('-', ':').ToUpperInvariant()));
        }
        return list;
    }

    /// <summary>Linux: `ip -6 neigh`, lines like "fe80::1 dev eth0 lladdr 00:11:22:33:44:55 router REACHABLE".</summary>
    private static async Task<List<(string Ip, string Mac)>> ReadNeighboursLinux(HashSet<string> interfaces, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ip", "-6 neigh show") { RedirectStandardOutput = true, UseShellExecute = false };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("the ip command didn't start (install iproute2)");
        var output = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var list = new List<(string, string)>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var at = Array.IndexOf(parts, "lladdr");
            if (parts.Length < 2 || at < 0 || at + 1 >= parts.Length) continue;
            if (line.Contains("FAILED") || line.Contains("INCOMPLETE")) continue;
            var dev = Array.IndexOf(parts, "dev");
            if (dev < 0 || dev + 1 >= parts.Length || !interfaces.Contains(parts[dev + 1])) continue;
            list.Add((parts[0], parts[at + 1].ToUpperInvariant()));
        }
        return list;
    }

    /// <summary>
    /// A MAC and address worth keeping: a real device's MAC (not multicast or
    /// empty) and a unicast address, link-local, unique-local or global.
    /// Returns the address without its zone.
    /// </summary>
    private static string? Usable(string mac, string ip)
    {
        var m = mac.ToUpperInvariant();
        if (m.Length != 17 || m == "00:00:00:00:00:00" || m == "FF:FF:FF:FF:FF:FF" || m.StartsWith("33:33:")) return null;
        if ((Convert.ToByte(m[..2], 16) & 1) == 1) return null;   // a group MAC
        var cut = ip.IndexOf('%');
        if (!IPAddress.TryParse(cut > 0 ? ip[..cut] : ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetworkV6) return null;
        if (addr.IsIPv6Multicast || addr.Equals(IPAddress.IPv6Any) || addr.Equals(IPAddress.IPv6Loopback)) return null;
        var b = addr.GetAddressBytes();
        var linkLocal = b[0] == 0xFE && (b[1] & 0xC0) == 0x80;
        var uniqueLocal = (b[0] & 0xFE) == 0xFC;
        var global = (b[0] & 0xE0) == 0x20;
        return linkLocal || uniqueLocal || global ? new IPAddress(b).ToString() : null;
    }
}
