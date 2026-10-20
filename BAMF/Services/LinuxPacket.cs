using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace LanWatch.Services;

/// <summary>
/// Raw packet sockets on Linux: whole Ethernet frames in and out of one interface with nothing between BAMF and the kernel, so the
/// active ARP scan and the traffic monitor need no libpcap. It is the same thing libpcap does underneath, an AF_PACKET socket, and it
/// needs the same permission, CAP_NET_RAW, which the service unit, the Docker run line and the add-on already give. Windows and macOS
/// have no such thing and keep using Npcap and libpcap.
/// </summary>
internal static class LinuxPacket
{
    public const int EthAll = 0x0003, EthArp = 0x0806;

    private const int AF_PACKET = 17, SOCK_RAW = 3, SOL_SOCKET = 1, SO_RCVTIMEO = 20, SOL_PACKET = 263, PACKET_ADD_MEMBERSHIP = 1, PACKET_MR_PROMISC = 1;
    private const int EINTR = 4, EAGAIN = 11, EPERM = 1, EACCES = 13, EAFNOSUPPORT = 97;

    [DllImport("libc", SetLastError = true)] private static extern int socket(int domain, int type, int protocol);
    [DllImport("libc", SetLastError = true)] private static extern int bind(int fd, byte[] addr, int len);
    [DllImport("libc", SetLastError = true)] private static extern nint sendto(int fd, byte[] buf, nuint len, int flags, byte[] addr, int addrLen);
    [DllImport("libc", SetLastError = true)] private static extern nint recv(int fd, byte[] buf, nuint len, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int setsockopt(int fd, int level, int optname, byte[] val, int len);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);

    private static readonly object Gate = new();
    private static bool? _available;
    private static string? _why;

    /// <summary>Whether this machine will give BAMF a raw packet socket. Asked once, by opening one.</summary>
    public static bool Available
    {
        get
        {
            if (!OperatingSystem.IsLinux()) return false;
            lock (Gate)
            {
                if (_available.HasValue) return _available.Value;
                try
                {
                    var link = Link.Open(0, EthArp, false, 100, out var errno);
                    if (link is null) { _available = false; _why = Reason(errno); }
                    else { link.Dispose(); _available = true; }
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { _available = false; _why = "this system has no C library BAMF can call"; }
                return _available.Value;
            }
        }
    }

    /// <summary>Why <see cref="Available"/> is false, in words for the person running BAMF.</summary>
    public static string? WhyNot { get { _ = Available; return _why; } }

    public static string Reason(int errno) => errno switch
    {
        EPERM or EACCES => "BAMF isn't allowed raw sockets here. Run it as root, or give it CAP_NET_RAW (the service unit, Docker's --cap-add NET_RAW and the add-on do)",
        EAFNOSUPPORT => "this kernel or container has no packet sockets",
        _ => $"a raw packet socket couldn't be opened (error {errno})",
    };

    /// <summary>The interface index of whichever interface has this address, or null.</summary>
    public static int? IndexOf(IPAddress localIp)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!nic.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(localIp))) continue;
            try { return nic.GetIPProperties().GetIPv4Properties().Index; } catch { return null; }
        }
        return null;
    }

    // ------------------------------------------------------------ the socket

    /// <summary>One packet socket, on one interface or (index 0) on all of them. Receive times out so a reader can notice it is time to stop.</summary>
    public sealed class Link : IDisposable
    {
        private int _fd;
        public int IfIndex { get; }
        private Link(int fd, int ifIndex) { _fd = fd; IfIndex = ifIndex; }

        /// <param name="protocol">The Ethernet type to hear (host order): <see cref="EthAll"/> for everything, <see cref="EthArp"/> for ARP.</param>
        /// <param name="promiscuous">Also hear frames meant for other machines, as a mirrored switch port sends them.</param>
        public static Link? Open(int ifIndex, int protocol, bool promiscuous, int timeoutMs, out int errno)
        {
            errno = 0;
            var fd = socket(AF_PACKET, SOCK_RAW, Htons(protocol));
            if (fd < 0) { errno = Marshal.GetLastWin32Error(); return null; }
            try
            {
                if (ifIndex != 0 && bind(fd, SockAddr(ifIndex, protocol, null), 20) < 0) { errno = Marshal.GetLastWin32Error(); close(fd); return null; }
                if (promiscuous && ifIndex != 0)
                {
                    // struct packet_mreq { int mr_ifindex; ushort mr_type; ushort mr_alen; uchar mr_address[8]; }
                    var mreq = new byte[16];
                    BitConverter.GetBytes(ifIndex).CopyTo(mreq, 0);
                    BitConverter.GetBytes((ushort)PACKET_MR_PROMISC).CopyTo(mreq, 4);
                    setsockopt(fd, SOL_PACKET, PACKET_ADD_MEMBERSHIP, mreq, mreq.Length);   // best effort: still hears what is meant for this machine
                }
                // struct timeval { long tv_sec; long tv_usec; }, as wide as a long is
                var tv = new byte[nint.Size * 2];
                var sec = timeoutMs / 1000; var usec = (timeoutMs % 1000) * 1000;
                if (nint.Size == 8) { BitConverter.GetBytes((long)sec).CopyTo(tv, 0); BitConverter.GetBytes((long)usec).CopyTo(tv, 8); }
                else { BitConverter.GetBytes(sec).CopyTo(tv, 0); BitConverter.GetBytes(usec).CopyTo(tv, 4); }
                setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, tv, tv.Length);
                return new Link(fd, ifIndex);
            }
            catch { close(fd); throw; }
        }

        /// <summary>Sends one whole Ethernet frame out of this interface to <paramref name="dstMac"/> (the frame carries it too).</summary>
        public bool Send(byte[] frame, byte[] dstMac, int protocol)
        {
            if (_fd < 0) return false;
            return sendto(_fd, frame, (nuint)frame.Length, 0, SockAddr(IfIndex, protocol, dstMac), 20) >= 0;
        }

        /// <summary>One frame into the buffer: how many bytes, 0 when the time ran out with nothing, -1 when the socket is gone.</summary>
        public int Receive(byte[] buffer)
        {
            if (_fd < 0) return -1;
            var n = recv(_fd, buffer, (nuint)buffer.Length, 0);
            if (n >= 0) return (int)n;
            var e = Marshal.GetLastWin32Error();
            return e is EAGAIN or EINTR ? 0 : -1;
        }

        public void Dispose()
        {
            var fd = Interlocked.Exchange(ref _fd, -1);
            if (fd >= 0) close(fd);
        }
    }

    private static int Htons(int v) => ((v & 0xFF) << 8) | ((v >> 8) & 0xFF);

    /// <summary>struct sockaddr_ll: family, protocol (network order), interface index, hardware type, packet type, address length and address.</summary>
    internal static byte[] SockAddr(int ifIndex, int protocol, byte[]? mac)
    {
        var a = new byte[20];
        BitConverter.GetBytes((ushort)AF_PACKET).CopyTo(a, 0);
        BitConverter.GetBytes((ushort)Htons(protocol)).CopyTo(a, 2);
        BitConverter.GetBytes(ifIndex).CopyTo(a, 4);
        if (mac is { Length: 6 }) { a[11] = 6; mac.CopyTo(a, 12); }
        return a;
    }

    // ------------------------------------------------------------ frames

    /// <summary>An ARP who-has from <paramref name="srcMac"/> and <paramref name="srcIp"/> for <paramref name="target"/>, broadcast, padded to the 60 bytes Ethernet wants.</summary>
    public static byte[] BuildArpRequest(byte[] srcMac, IPAddress srcIp, IPAddress target)
    {
        var f = new byte[60];
        for (var i = 0; i < 6; i++) f[i] = 0xFF;
        srcMac.CopyTo(f, 6);
        f[12] = 0x08; f[13] = 0x06;                         // ARP
        f[14] = 0; f[15] = 1;                               // Ethernet
        f[16] = 0x08; f[17] = 0x00;                         // IPv4
        f[18] = 6; f[19] = 4;
        f[20] = 0; f[21] = 1;                               // request
        srcMac.CopyTo(f, 22);
        srcIp.GetAddressBytes().CopyTo(f, 28);
        // target hardware address stays zero
        target.GetAddressBytes().CopyTo(f, 38);
        return f;
    }

    /// <summary>Reads an ARP reply (not a request) out of a frame: who it says it is, and the hardware address.</summary>
    public static bool TryParseArpReply(byte[] f, int len, out IPAddress ip, out string mac)
    {
        ip = IPAddress.None; mac = "";
        var off = 14;
        if (len >= 18 && f[12] == 0x81 && f[13] == 0x00) off = 18;                          // a VLAN tag
        if (len < off + 28 || f[off - 2] != 0x08 || f[off - 1] != 0x06) return false;       // not ARP
        if (f[off + 4] != 6 || f[off + 5] != 4 || f[off + 2] != 0x08 || f[off + 3] != 0x00) return false;
        if (f[off + 6] != 0 || f[off + 7] != 2) return false;                              // not a reply
        ip = new IPAddress(new ReadOnlySpan<byte>(f, off + 14, 4));
        mac = string.Join(":", Enumerable.Range(off + 8, 6).Select(i => f[i].ToString("X2")));
        return true;
    }

    /// <summary>What the traffic monitor wants of the wire: IPv4, ARP and IPv6's ICMP, the same as libpcap's filter "ip or arp or icmp6".</summary>
    public static bool WantedFrame(byte[] d, int len)
    {
        if (len < 14) return false;
        var off = 14; var type = (d[12] << 8) | d[13];
        if (type == 0x8100 && len >= 18) { type = (d[16] << 8) | d[17]; off = 18; }
        return type is 0x0800 or 0x0806 || (type == 0x86DD && len >= off + 7 && d[off + 6] == 58);
    }
}
