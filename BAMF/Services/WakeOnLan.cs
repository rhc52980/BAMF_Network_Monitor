using System.Net;
using System.Net.Sockets;

namespace LanWatch.Services;

/// <summary>Sends Wake-on-LAN magic packets.</summary>
public static class WakeOnLan
{
    /// <summary>
    /// Broadcasts a magic packet for the given MAC. Sends to the global
    /// broadcast and, when a subnet is supplied, its directed broadcast too
    /// (directed broadcast is what actually crosses to the target's segment
    /// when the server has an interface there).
    /// </summary>
    public static async Task<bool> WakeAsync(string mac, IPAddress? directedBroadcast = null)
    {
        var macBytes = ParseMac(mac);
        if (macBytes is null) return false;

        // Magic packet: 6x 0xFF, then the MAC repeated 16 times.
        var packet = new byte[102];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var i = 6; i < 102; i += 6) Array.Copy(macBytes, 0, packet, i, 6);

        var sent = false;
        using var udp = new UdpClient { EnableBroadcast = true };

        foreach (var target in Targets(directedBroadcast))
        {
            try
            {
                await udp.SendAsync(packet, packet.Length, new IPEndPoint(target, 9));
                sent = true;
            }
            catch { /* try the next target */ }
        }
        return sent;
    }

    private static IEnumerable<IPAddress> Targets(IPAddress? directed)
    {
        yield return IPAddress.Broadcast;              // 255.255.255.255
        if (directed is not null) yield return directed;
    }

    private static byte[]? ParseMac(string mac)
    {
        var clean = mac.Replace(":", "").Replace("-", "").Replace(".", "");
        if (clean.Length != 12) return null;
        try
        {
            var bytes = new byte[6];
            for (var i = 0; i < 6; i++)
                bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
            return bytes;
        }
        catch { return null; }
    }

    /// <summary>Directed broadcast address for a network/prefix (e.g. 192.168.1.255).</summary>
    public static IPAddress DirectedBroadcast(IPAddress network, int prefix)
    {
        var n = network.GetAddressBytes();
        uint netU = (uint)(n[0] << 24 | n[1] << 16 | n[2] << 8 | n[3]);
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        uint bcast = netU | ~mask;
        return new IPAddress(new[] { (byte)(bcast >> 24), (byte)(bcast >> 16), (byte)(bcast >> 8), (byte)bcast });
    }
}
