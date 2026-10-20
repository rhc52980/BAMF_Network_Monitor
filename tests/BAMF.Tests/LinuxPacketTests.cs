using System.Net;
using System.Net.NetworkInformation;
using LanWatch.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BAMF.Tests;

/// <summary>
/// Raw packet sockets on Linux, which the active ARP scan and the traffic monitor use there in place of libpcap. The frames are tested
/// everywhere. The sockets are tested only where a pair of linked interfaces has been made for them (the CI job makes one with
/// `ip link add ... type veth`, and says which in BAMF_RAW_IF_A, BAMF_RAW_IP_A, BAMF_RAW_IF_B and BAMF_RAW_IP_B): the scan is run on one
/// end and the kernel on the other answers it, and a DHCP offer sent on one end is heard by the traffic monitor on the other.
/// </summary>
public class LinuxPacketTests
{
    private static readonly byte[] MacA = { 0x02, 0xAA, 0x00, 0x00, 0x00, 0x01 };

    // ---------- the frames ----------

    [Fact]
    public void An_arp_request_is_a_broadcast_who_has_with_the_senders_address_and_no_target_hardware_address()
    {
        var f = LinuxPacket.BuildArpRequest(MacA, IPAddress.Parse("10.77.0.2"), IPAddress.Parse("10.77.0.9"));
        Assert.Equal(60, f.Length);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, f[0..6]);
        Assert.Equal(MacA, f[6..12]);
        Assert.Equal(new byte[] { 0x08, 0x06 }, f[12..14]);                    // ARP
        Assert.Equal(new byte[] { 0, 1, 8, 0, 6, 4, 0, 1 }, f[14..22]);          // Ethernet, IPv4, 6 and 4 bytes, a request
        Assert.Equal(MacA, f[22..28]);
        Assert.Equal(new byte[] { 10, 77, 0, 2 }, f[28..32]);
        Assert.Equal(new byte[6], f[32..38]);
        Assert.Equal(new byte[] { 10, 77, 0, 9 }, f[38..42]);
        Assert.Equal(new byte[18], f[42..60]);                                  // padding
        // a request is not a reply
        Assert.False(LinuxPacket.TryParseArpReply(f, f.Length, out _, out _));
    }

    private static byte[] Reply(bool vlan = false)
    {
        var off = vlan ? 18 : 14;
        var f = new byte[off + 28];
        MacA.CopyTo(f, 0); new byte[] { 0x02, 0xBB, 0, 0, 0, 0x09 }.CopyTo(f, 6);
        if (vlan) { f[12] = 0x81; f[13] = 0x00; f[16] = 0x08; f[17] = 0x06; } else { f[12] = 0x08; f[13] = 0x06; }
        new byte[] { 0, 1, 8, 0, 6, 4, 0, 2 }.CopyTo(f, off);                   // a reply
        new byte[] { 0x02, 0xBB, 0, 0, 0, 0x09 }.CopyTo(f, off + 8);
        new byte[] { 10, 77, 0, 9 }.CopyTo(f, off + 14);
        MacA.CopyTo(f, off + 18);
        new byte[] { 10, 77, 0, 2 }.CopyTo(f, off + 24);
        return f;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_arp_reply_gives_the_address_and_the_hardware_address_it_claims(bool vlan)
    {
        var f = Reply(vlan);
        Assert.True(LinuxPacket.TryParseArpReply(f, f.Length, out var ip, out var mac));
        Assert.Equal("10.77.0.9", ip.ToString());
        Assert.Equal("02:BB:00:00:00:09", mac);
    }

    [Fact]
    public void Anything_that_is_not_an_arp_reply_is_nothing()
    {
        var f = Reply();
        Assert.False(LinuxPacket.TryParseArpReply(f, 20, out _, out _));        // cut short
        var notArp = (byte[])f.Clone(); notArp[12] = 0x08; notArp[13] = 0x00;
        Assert.False(LinuxPacket.TryParseArpReply(notArp, notArp.Length, out _, out _));
        var ipv6 = (byte[])f.Clone(); ipv6[14 + 2] = 0x86; ipv6[14 + 3] = 0xDD;   // not an IPv4 protocol address
        Assert.False(LinuxPacket.TryParseArpReply(ipv6, ipv6.Length, out _, out _));
        var wrongSize = (byte[])f.Clone(); wrongSize[14 + 4] = 8;
        Assert.False(LinuxPacket.TryParseArpReply(wrongSize, wrongSize.Length, out _, out _));
    }

    [Fact]
    public void The_traffic_monitor_wants_ipv4_arp_and_icmpv6_and_nothing_else_as_libpcaps_filter_did()
    {
        byte[] Frame(int type, int len = 60, byte next = 0, int off = 14)
        {
            var f = new byte[len]; f[12] = (byte)(type >> 8); f[13] = (byte)type;
            if (off == 18) { f[12] = 0x81; f[13] = 0x00; f[16] = (byte)(type >> 8); f[17] = (byte)type; }
            if (len > off + 6) f[off + 6] = next;
            return f;
        }
        Assert.True(LinuxPacket.WantedFrame(Frame(0x0800), 60));
        Assert.True(LinuxPacket.WantedFrame(Frame(0x0806), 60));
        Assert.True(LinuxPacket.WantedFrame(Frame(0x86DD, 80, 58), 80));         // IPv6 carrying ICMPv6
        Assert.False(LinuxPacket.WantedFrame(Frame(0x86DD, 80, 6), 80));         // IPv6 carrying TCP
        Assert.True(LinuxPacket.WantedFrame(Frame(0x0800, 60, 0, 18), 60));      // behind a VLAN tag
        Assert.True(LinuxPacket.WantedFrame(Frame(0x86DD, 80, 58, 18), 80));
        Assert.False(LinuxPacket.WantedFrame(Frame(0x88CC), 60));                // LLDP
        Assert.False(LinuxPacket.WantedFrame(Frame(0x0800), 10));                // too short to be a frame
    }

    [Fact]
    public void A_packet_address_is_laid_out_as_the_kernel_reads_it()
    {
        var a = LinuxPacket.SockAddr(7, LinuxPacket.EthArp, new byte[] { 1, 2, 3, 4, 5, 6 });
        Assert.Equal(20, a.Length);
        Assert.Equal(17, BitConverter.ToUInt16(a, 0));                              // AF_PACKET
        Assert.Equal(new byte[] { 0x08, 0x06 }, a[2..4]);                           // the protocol, in network order
        Assert.Equal(7, BitConverter.ToInt32(a, 4));
        Assert.Equal(6, a[11]);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, a[12..18]);
        var bound = LinuxPacket.SockAddr(7, LinuxPacket.EthAll, null);
        Assert.Equal(0, bound[11]);
        Assert.Equal(new byte[] { 0x00, 0x03 }, bound[2..4]);
    }

    [Fact]
    public void Each_refusal_says_what_to_do_about_it()
    {
        Assert.Contains("CAP_NET_RAW", LinuxPacket.Reason(1));
        Assert.Contains("CAP_NET_RAW", LinuxPacket.Reason(13));
        Assert.Contains("no packet sockets", LinuxPacket.Reason(97));
        Assert.Contains("error 5", LinuxPacket.Reason(5));
    }

    [Fact]
    public void Away_from_linux_there_are_no_raw_sockets_and_the_old_ways_are_what_is_said()
    {
        if (OperatingSystem.IsLinux()) return;
        Assert.False(LinuxPacket.Available);
        Assert.Null(LinuxPacket.WhyNot);
        Assert.Equal("Npcap", ArpScanner.Via);
        Assert.Contains("Npcap", ArpScanner.WhyNot);
    }

    // ---------- the sockets, where there is a pair of linked interfaces to try them on ----------

    private static (string IfA, IPAddress IpA, string IfB, IPAddress IpB)? Pair()
    {
        var env = (string k) => Environment.GetEnvironmentVariable(k);
        if (!OperatingSystem.IsLinux() || env("BAMF_RAW_IF_A") is not { Length: > 0 } a || env("BAMF_RAW_IP_A") is not { Length: > 0 } ia
            || env("BAMF_RAW_IF_B") is not { Length: > 0 } b || env("BAMF_RAW_IP_B") is not { Length: > 0 } ib) return null;
        return (a, IPAddress.Parse(ia), b, IPAddress.Parse(ib));
    }

    [Fact]
    [Trait("Category", "RawSockets")]
    public void Where_the_ci_asks_for_raw_sockets_it_gets_them()
    {
        if (Environment.GetEnvironmentVariable("BAMF_REQUIRE_RAW") != "1") return;
        Assert.True(LinuxPacket.Available, LinuxPacket.WhyNot);
        Assert.Equal("raw sockets", ArpScanner.Via);
        Assert.True(ArpScanner.IsAvailable);
    }

    [Fact]
    [Trait("Category", "RawSockets")]
    public async Task An_arp_scan_on_one_end_of_a_link_finds_the_kernel_answering_on_the_other()
    {
        if (Pair() is not { } p || !LinuxPacket.Available) return;
        var mac = NetworkInterface.GetAllNetworkInterfaces().Single(n => n.Name == p.IfA).GetPhysicalAddress().GetAddressBytes();
        var macB = string.Join(":", NetworkInterface.GetAllNetworkInterfaces().Single(n => n.Name == p.IfB).GetPhysicalAddress().GetAddressBytes().Select(x => x.ToString("X2")));
        var targets = Enumerable.Range(1, 20).Select(i => IPAddress.Parse($"10.77.0.{i}")).ToList();       // the other end is .1; the rest are nobody
        var found = await ArpScanner.ScanRawAsync(targets, p.IpA, mac, 400, CancellationToken.None);
        Assert.Contains(found, f => f.Ip.Equals(p.IpB) && f.Mac == macB);
        Assert.DoesNotContain(found, f => f.Ip.Equals(p.IpA));
    }

    private static byte[] DhcpOfferFrame(byte[] srcMac, string server)
    {
        var dhcp = new byte[300];
        dhcp[0] = 2; dhcp[1] = 1; dhcp[2] = 6;
        new byte[] { 0x63, 0x82, 0x53, 0x63 }.CopyTo(dhcp, 236);
        var o = 240;
        dhcp[o++] = 53; dhcp[o++] = 1; dhcp[o++] = 2;
        dhcp[o++] = 54; dhcp[o++] = 4; IPAddress.Parse(server).GetAddressBytes().CopyTo(dhcp, o); o += 4;
        dhcp[o] = 255;
        var udpLen = 8 + dhcp.Length; var ipLen = 20 + udpLen;
        var f = new byte[14 + ipLen];
        for (var i = 0; i < 6; i++) f[i] = 0xFF;
        srcMac.CopyTo(f, 6); f[12] = 0x08; f[13] = 0x00;
        f[14] = 0x45; f[16] = (byte)(ipLen >> 8); f[17] = (byte)ipLen; f[22] = 64; f[23] = 17;
        IPAddress.Parse(server).GetAddressBytes().CopyTo(f, 26); IPAddress.Broadcast.GetAddressBytes().CopyTo(f, 30);
        f[34] = 0; f[35] = 67; f[36] = 0; f[37] = 68; f[38] = (byte)(udpLen >> 8); f[39] = (byte)udpLen;
        dhcp.CopyTo(f, 42);
        return f;
    }

    [Fact]
    [Trait("Category", "RawSockets")]
    public async Task The_traffic_monitor_hears_what_is_sent_on_the_other_end_of_a_link()
    {
        if (Pair() is not { } p || !LinuxPacket.Available) return;
        using var tm = new TrafficMonitor(NullLogger<TrafficMonitor>.Instance, new HostStore(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bamf:DatabasePath"] = Path.Combine(Path.GetTempPath(), "bamf-raw-" + Guid.NewGuid().ToString("N") + ".db") }).Build()));
        tm.EnsureRunning(true, new[] { p.IpB });
        Assert.True(tm.Running, tm.LastError);
        Assert.Equal(new[] { p.IpB.ToString() }, tm.Interfaces);

        // sent from the A end: it comes out of the link on the B end, where the monitor is listening
        var srcMac = NetworkInterface.GetAllNetworkInterfaces().Single(n => n.Name == p.IfA).GetPhysicalAddress().GetAddressBytes();
        using var send = LinuxPacket.Link.Open(LinuxPacket.IndexOf(p.IpA)!.Value, LinuxPacket.EthAll, false, 100, out var errno)!;
        Assert.NotNull(send);
        var before = tm.Frames;
        for (var i = 0; i < 5; i++) { Assert.True(send.Send(DhcpOfferFrame(srcMac, "10.77.0.250"), new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, 0x0800)); await Task.Delay(50); }
        await Task.Delay(600);
        Assert.True(tm.Frames > before, "no frames were heard");
        Assert.Contains(tm.DhcpServers(), s => s.Ip == "10.77.0.250");
        tm.EnsureRunning(false, new[] { p.IpB });
        Assert.False(tm.Running);
    }
}
