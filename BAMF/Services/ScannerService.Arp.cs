using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace LanWatch.Services;

/// <summary>Reading this machine's ARP table, on Windows and Linux.</summary>
public partial class ScannerService
{
    // ---------------- ARP ----------------

    private async Task<List<(IPAddress Ip, string Mac)>> ReadArpTable(IPAddress network, int prefix)
    {
        var raw = OperatingSystem.IsWindows()
            ? await ReadArpTableWindows()
            : ReadArpTableLinux();

        var results = new List<(IPAddress, string)>();
        foreach (var (ip, mac) in raw)
        {
            // Skip broadcast/multicast pseudo-entries.
            if (mac is "FF:FF:FF:FF:FF:FF") continue;
            if (mac.StartsWith("01:00:5E") || mac.StartsWith("33:33")) continue;
            if (mac is "00:00:00:00:00:00") continue;

            // Only keep hosts inside the target subnet.
            if (!InSubnet(ip, network, prefix)) continue;

            results.Add((ip, mac));
        }

        // Include this machine itself (it never appears in its own ARP table).
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (!InSubnet(addr.Address, network, prefix)) continue;
                var mac = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                if (mac.Length == 17)
                    results.Add((addr.Address, mac));
            }
        }

        return results
            .GroupBy(r => r.Item2)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>Windows: parse `arp -a` output, dynamic entries only.</summary>
    private static async Task<List<(IPAddress Ip, string Mac)>> ReadArpTableWindows()
    {
        var results = new List<(IPAddress, string)>();

        var psi = new ProcessStartInfo
        {
            FileName = "arp",
            Arguments = "-a",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.ASCII,
        };
        using var proc = Process.Start(psi);
        if (proc is null) return results;
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();

        // Lines look like:  192.168.1.10          00-11-32-9f-44-2a     dynamic
        foreach (Match m in ArpLine().Matches(output))
        {
            if (!IPAddress.TryParse(m.Groups[1].Value, out var ip)) continue;
            if (!m.Groups[3].Value.Contains("dynamic", StringComparison.OrdinalIgnoreCase)) continue;
            results.Add((ip, m.Groups[2].Value.Replace('-', ':').ToUpperInvariant()));
        }
        return results;
    }

    /// <summary>Linux: parse /proc/net/arp. Flag 0x2 = complete entry.</summary>
    private static List<(IPAddress Ip, string Mac)> ReadArpTableLinux()
    {
        var results = new List<(IPAddress, string)>();
        const string path = "/proc/net/arp";
        if (!File.Exists(path)) return results;

        // Columns: IP address  HW type  Flags  HW address  Mask  Device
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            if (!IPAddress.TryParse(parts[0], out var ip)) continue;

            int flags;
            try { flags = Convert.ToInt32(parts[2], 16); }
            catch { continue; }
            if ((flags & 0x2) == 0) continue; // incomplete entry

            results.Add((ip, parts[3].ToUpperInvariant()));
        }
        return results;
    }

    [GeneratedRegex(@"^\s*(\d{1,3}(?:\.\d{1,3}){3})\s+([0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex ArpLine();
}
