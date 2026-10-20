using System.Net;
using System.Net.NetworkInformation;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace LanWatch.Services;

/// <summary>
/// Active ARP scanning: sends a who-has request for every address in the
/// subnet and collects replies. Catches hosts that ignore ping. On Linux it
/// uses a raw packet socket (needs CAP_NET_RAW, no package); on Windows and
/// macOS it uses Npcap/libpcap through SharpPcap (the driver from
/// https://npcap.com on Windows). If neither is there, <see cref="IsAvailable"/>
/// is false and callers should fall back to ping sweep.
/// </summary>
public static class ArpScanner
{
    private static bool? _available;
    private static readonly object _availLock = new();

    /// <summary>Whether ARP frames can be sent and heard here by any means.</summary>
    public static bool IsAvailable => LinuxPacket.Available || PcapAvailable;

    /// <summary>What does the capturing here: "raw sockets" on Linux, "Npcap" elsewhere.</summary>
    public static string Via => OperatingSystem.IsLinux() && LinuxPacket.Available ? "raw sockets" : "Npcap";

    /// <summary>Why not, in words, when <see cref="IsAvailable"/> is false.</summary>
    public static string WhyNot => OperatingSystem.IsLinux() ? LinuxPacket.WhyNot ?? "no packet capture is available" : "the Npcap driver isn't installed";

    private static bool PcapAvailable
    {
        get
        {
            lock (_availLock)
            {
                if (_available.HasValue) return _available.Value;
                try
                {
                    // Touching the device list throws if the Npcap driver isn't installed.
                    _ = LibPcapLiveDeviceList.Instance.Count;
                    _available = true;
                }
                catch
                {
                    _available = false;
                }
                return _available.Value;
            }
        }
    }

    /// <summary>
    /// Scans the subnet with raw ARP requests from the interface owning
    /// <paramref name="localIp"/>. Returns every (IP, MAC) that replied.
    /// Throws if no pcap device matches the local IP.
    /// </summary>
    public static async Task<List<(IPAddress Ip, string Mac)>> ScanAsync(
        IEnumerable<IPAddress> addresses,
        IPAddress localIp,
        PhysicalAddress localMac,
        int listenExtraMs,
        CancellationToken ct)
    {
        if (LinuxPacket.Available) return await ScanRawAsync(addresses, localIp, localMac.GetAddressBytes(), listenExtraMs, ct);
        var device = FindDevice(localIp)
            ?? throw new InvalidOperationException($"No pcap device owns {localIp}");

        var results = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        var resultsLock = new object();

        var broadcast = PhysicalAddress.Parse("FF-FF-FF-FF-FF-FF");
        var zeroMac = PhysicalAddress.Parse("00-00-00-00-00-00");

        device.Open(DeviceModes.Promiscuous, 50);
        try
        {
            device.Filter = "arp";

            device.OnPacketArrival += (_, e) =>
            {
                try
                {
                    var raw = e.GetPacket();
                    var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
                    var arp = packet.Extract<ArpPacket>();
                    if (arp is null) return;
                    if (arp.Operation != ArpOperation.Response) return;

                    var mac = FormatMac(arp.SenderHardwareAddress);
                    lock (resultsLock)
                        results[mac] = arp.SenderProtocolAddress;
                }
                catch { /* malformed packet, skip */ }
            };
            device.StartCapture();

            // Fire a who-has at every address, lightly throttled to avoid drops.
            var sent = 0;
            foreach (var target in addresses)
            {
                ct.ThrowIfCancellationRequested();
                if (target.Equals(localIp)) continue;

                var eth = new EthernetPacket(localMac, broadcast, EthernetType.Arp);
                var arp = new ArpPacket(ArpOperation.Request, zeroMac, target, localMac, localIp);
                eth.PayloadPacket = arp;
                device.SendPacket(eth);

                if (++sent % 50 == 0)
                    await Task.Delay(20, ct);
            }

            // Give stragglers time to answer.
            await Task.Delay(listenExtraMs, ct);
            device.StopCapture();
        }
        finally
        {
            device.Close();
        }

        lock (resultsLock)
            return results.Select(kv => (kv.Value, kv.Key)).ToList();
    }

    /// <summary>The same scan on a Linux packet socket: a who-has to every address, and every ARP reply heard until a little after the last.</summary>
    internal static async Task<List<(IPAddress Ip, string Mac)>> ScanRawAsync(
        IEnumerable<IPAddress> addresses, IPAddress localIp, byte[] localMac, int listenExtraMs, CancellationToken ct, int? ifIndex = null)
    {
        var index = ifIndex ?? LinuxPacket.IndexOf(localIp) ?? throw new InvalidOperationException($"No interface owns {localIp}");
        using var link = LinuxPacket.Link.Open(index, LinuxPacket.EthArp, false, 100, out var errno)
            ?? throw new InvalidOperationException(LinuxPacket.Reason(errno));

        var results = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        var resultsLock = new object();
        var stop = false;
        var reader = Task.Run(() =>
        {
            var buf = new byte[2048];
            while (!Volatile.Read(ref stop))
            {
                var n = link.Receive(buf);
                if (n < 0) break;
                if (n == 0) continue;
                if (LinuxPacket.TryParseArpReply(buf, n, out var ip, out var mac))
                    lock (resultsLock) results[mac] = ip;
            }
        });
        try
        {
            var broadcast = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
            var sent = 0;
            foreach (var target in addresses)
            {
                ct.ThrowIfCancellationRequested();
                if (target.Equals(localIp)) continue;
                link.Send(LinuxPacket.BuildArpRequest(localMac, localIp, target), broadcast, LinuxPacket.EthArp);
                if (++sent % 50 == 0) await Task.Delay(20, ct);
            }
            await Task.Delay(listenExtraMs, ct);
        }
        finally
        {
            Volatile.Write(ref stop, true);
            await reader;            // a receive lasts at most its 100 ms timeout, so this is quick, and the socket outlives it
        }
        lock (resultsLock)
            return results.Select(kv => (kv.Value, kv.Key)).ToList();
    }

    private static LibPcapLiveDevice? FindDevice(IPAddress localIp)
    {
        foreach (var dev in LibPcapLiveDeviceList.Instance)
        {
            foreach (var addr in dev.Addresses)
            {
                if (addr.Addr?.ipAddress is { } ip && ip.Equals(localIp))
                    return dev;
            }
        }
        return null;
    }

    private static string FormatMac(PhysicalAddress mac) =>
        string.Join(":", mac.GetAddressBytes().Select(b => b.ToString("X2")));
}
