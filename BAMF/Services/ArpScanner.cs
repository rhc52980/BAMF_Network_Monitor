using System.Net;
using System.Net.NetworkInformation;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace LanWatch.Services;

/// <summary>
/// Active ARP scanning via Npcap/SharpPcap: sends a who-has request for every
/// address in the subnet and collects replies. Catches hosts that ignore ping.
/// Requires the Npcap driver (https://npcap.com). If the driver is missing,
/// <see cref="IsAvailable"/> is false and callers should fall back to ping sweep.
/// </summary>
public static class ArpScanner
{
    private static bool? _available;
    private static readonly object _availLock = new();

    public static bool IsAvailable
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
