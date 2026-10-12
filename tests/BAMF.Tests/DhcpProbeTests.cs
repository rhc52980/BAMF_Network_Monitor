using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>
/// The DHCP check: the question BAMF asks, the offers it reads back (gateway, DNS, mask, domain, lease), what it does with the
/// servers that answer, and its switches. Made-up packets, and for the sockets one local server standing in for a router.
/// </summary>
[Collection("Endpoints")]
public class DhcpProbeTests
{
    private static readonly byte[] Cookie = { 0x63, 0x82, 0x53, 0x63 };

    /// <summary>An OFFER as a server would send it.</summary>
    private static byte[] Offer(uint xid, string server, string offered, byte[]? chaddr = null, int type = 2, string[]? gateways = null, string[]? dns = null,
        string? mask = "255.255.255.0", string? domain = "home.lan", uint lease = 1800)
    {
        var p = new byte[400];
        p[0] = 2; p[1] = 1; p[2] = 6;
        p[4] = (byte)(xid >> 24); p[5] = (byte)(xid >> 16); p[6] = (byte)(xid >> 8); p[7] = (byte)xid;
        IPAddress.Parse(offered).GetAddressBytes().CopyTo(p, 16);
        (chaddr ?? DhcpProbe.ClientMac).CopyTo(p, 28);
        Cookie.CopyTo(p, 236);
        var o = 240;
        void Opt(byte code, params byte[] v) { p[o++] = code; p[o++] = (byte)v.Length; v.CopyTo(p, o); o += v.Length; }
        Opt(53, (byte)type);
        Opt(54, IPAddress.Parse(server).GetAddressBytes());
        if (mask is not null) Opt(1, IPAddress.Parse(mask).GetAddressBytes());
        if (gateways is not null) Opt(3, gateways.SelectMany(g => IPAddress.Parse(g).GetAddressBytes()).ToArray());
        if (dns is not null) Opt(6, dns.SelectMany(g => IPAddress.Parse(g).GetAddressBytes()).ToArray());
        if (domain is not null) Opt(15, System.Text.Encoding.ASCII.GetBytes(domain));
        Opt(51, (byte)(lease >> 24), (byte)(lease >> 16), (byte)(lease >> 8), (byte)lease);
        p[o] = 255;
        return p;
    }

    // ---------- the packets ----------

    [Fact]
    public void The_question_is_a_discover_from_a_made_up_client_asking_for_what_is_worth_reading()
    {
        var p = DhcpProbe.BuildDiscover(0x01020304);
        Assert.True(p.Length >= 300);                                         // some servers refuse a runt
        Assert.Equal(1, p[0]);                                                // a request
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, p[4..8]);
        Assert.Equal(0x80, p[10]);                                            // the broadcast flag
        Assert.Equal(DhcpProbe.ClientMac, p[28..34]);
        Assert.Equal(Cookie, p[236..240]);
        Assert.Equal(new byte[] { 53, 1, 1 }, p[240..243]);                    // DISCOVER, and never a request
        Assert.Contains((byte)255, p);
        // the made-up address is locally administered and is not a real adapter's
        Assert.Equal(0x02, DhcpProbe.ClientMac[0] & 0x03);
        // the parameter list asks for the router and the DNS servers
        var s = BitConverter.ToString(p);
        Assert.Contains("37-08-01-03-06-0F-1C-2A-33-36", s);
    }

    [Fact]
    public void An_offer_is_read_for_its_server_gateway_dns_mask_domain_and_lease()
    {
        var bytes = Offer(0xAABBCCDD, "192.168.30.254", "192.168.30.128", gateways: new[] { "192.168.30.2" }, dns: new[] { "192.168.30.2", "1.1.1.1" }, lease: 7200);
        var r = DhcpProbe.Parse(bytes, bytes.Length)!;
        Assert.Equal(0xAABBCCDDu, r.Xid);
        Assert.Equal(2, r.Type);
        Assert.Equal("192.168.30.254", r.Server);
        Assert.Equal("192.168.30.128", r.Offered);
        Assert.Equal("255.255.255.0", r.Mask);
        Assert.Equal(new[] { "192.168.30.2" }, r.Gateways);
        Assert.Equal(new[] { "192.168.30.2", "1.1.1.1" }, r.Dns);
        Assert.Equal("home.lan", r.Domain);
        Assert.Equal(7200, r.LeaseSeconds);
        Assert.Equal(DhcpProbe.ClientMac, r.Chaddr);
        // what isn't a reply, or is cut short, is nothing
        Assert.Null(DhcpProbe.Parse(DhcpProbe.BuildDiscover(1), 300));       // a request, not a reply
        Assert.Null(DhcpProbe.Parse(new byte[100], 100));
        var bad = (byte[])bytes.Clone(); bad[236] = 0;
        Assert.Null(DhcpProbe.Parse(bad, bad.Length));                       // no magic cookie
    }

    [Theory]
    [InlineData("192.168.30.1", 24, "192.168.30.255")]
    [InlineData("10.1.2.3", 16, "10.1.255.255")]
    [InlineData("172.16.5.9", 20, "172.16.15.255")]
    [InlineData("192.168.1.50", 30, "192.168.1.51")]
    public void A_networks_broadcast_address_has_every_host_bit_set(string self, int prefix, string expected) =>
        Assert.Equal(expected, DhcpProbe.DirectedBroadcast(IPAddress.Parse(self), prefix).ToString());

    // ---------- a round ----------

    private static IReadOnlyDictionary<string, ScannerService.NetworkPlace> Places() => new Dictionary<string, ScannerService.NetworkPlace>
    {
        ["192.168.30.0/24"] = new("192.168.30.2", "AA:BB:CC:00:00:02", "192.168.30.1"),
        ["192.168.209.0/24"] = new("192.168.209.1", "AA:BB:CC:00:00:09", null),
        ["10.9.0.0/24"] = new(null, null, null),            // no interface here
    };

    [Fact]
    public async Task Every_server_that_answers_is_noted_with_what_its_offer_says_and_the_rest_is_left_out()
    {
        using var app = new BamfApp();
        var probe = app.Services.GetRequiredService<DhcpProbe>();
        var traffic = app.Services.GetRequiredService<TrafficMonitor>();
        probe.Places = Places;
        IReadOnlyList<DhcpProbe.Question>? asked = null;
        probe.Exchange = (qs, _) =>
        {
            asked = qs;
            var a = qs.Single(q => q.Subnet == "192.168.30.0/24").Xid;
            var packets = new List<DhcpProbe.Heard>
            {
                new("192.168.30.1", Offer(a, "192.168.30.1", "192.168.30.130", gateways: new[] { "192.168.30.1" }, dns: new[] { "192.168.30.1" })),
                new("192.168.30.77", Offer(a, "192.168.30.77", "192.168.30.131", gateways: new[] { "192.168.30.77" }, dns: new[] { "8.8.8.8" })),       // a second server
                new("192.168.30.1", Offer(a, "192.168.30.1", "192.168.30.130")),                                                                       // the same one again
                new("192.168.30.9", Offer(a + 1, "192.168.30.9", "192.168.30.140")),                                                                   // not an answer to this question
                new("192.168.30.8", Offer(a, "192.168.30.8", "192.168.30.141", chaddr: new byte[] { 2, 0, 0, 0, 0, 1 })),                              // answering someone else
                new("192.168.30.6", Offer(a, "192.168.30.6", "192.168.30.142", type: 6)),                                                              // a refusal, not an offer
            };
            return Task.FromResult((packets, (string?)null, new Dictionary<string, string>()));
        };

        var r = (await probe.RunOnce(CancellationToken.None))!;
        Assert.Equal(2, asked!.Count);                                         // the network with no interface isn't asked
        Assert.All(asked, q => Assert.Equal(DhcpProbe.ClientMac, q.Packet[28..34]));
        Assert.Equal(3, r.Networks.Count);

        var net = r.Networks.Single(n => n.Subnet == "192.168.30.0/24");
        Assert.Equal(new[] { "192.168.30.1", "192.168.30.77" }, net.Offers.Select(o => o.Server));
        var first = net.Offers[0];
        Assert.Equal("192.168.30.130", first.Offered);
        Assert.Equal(new[] { "192.168.30.1" }, first.Gateways);
        Assert.Equal(new[] { "192.168.30.1" }, first.Dns);
        Assert.Equal("home.lan", first.Domain);
        Assert.Equal(1800, first.LeaseSeconds);
        Assert.Empty(r.Networks.Single(n => n.Subnet == "192.168.209.0/24").Offers);                           // asked, nobody answered
        Assert.Contains("no address", r.Networks.Single(n => n.Subnet == "10.9.0.0/24").Error);

        // the servers are known to the traffic monitor, and the answer is kept
        Assert.Equal(new[] { "192.168.30.1", "192.168.30.77" }, traffic.DhcpServers().Select(s => s.Ip));
        Assert.Contains("192.168.30.1", traffic.KnownDhcp);
        Assert.Equal(r.At, probe.Last!.At);
    }

    [Fact]
    public async Task A_server_not_seen_before_alerts_like_one_overheard_and_the_first_is_learned_quietly()
    {
        using var app = new BamfApp();
        var probe = app.Services.GetRequiredService<DhcpProbe>();
        var traffic = app.Services.GetRequiredService<TrafficMonitor>();
        probe.Places = Places;
        var round = 0;
        probe.Exchange = (qs, _) =>
        {
            var a = qs.Single(q => q.Subnet == "192.168.30.0/24").Xid;
            var servers = round++ == 0 ? new[] { "192.168.30.1" } : new[] { "192.168.30.1", "192.168.30.66" };
            var heard = servers.Select(s => new DhcpProbe.Heard(s, Offer(a, s, "192.168.30.150"))).ToList();
            return Task.FromResult((heard, (string?)null, new Dictionary<string, string>()));
        };
        await probe.RunOnce(CancellationToken.None);
        Assert.Empty(traffic.Alerts().Where(x => x.Kind == "dhcp"));           // learned, not alerted
        await probe.RunOnce(CancellationToken.None);
        var alert = Assert.Single(traffic.Alerts().Where(x => x.Kind == "dhcp"));
        Assert.Contains("192.168.30.66", alert.Title);
    }

    [Fact]
    public async Task A_socket_that_cannot_listen_is_said_plainly_and_the_round_still_ends()
    {
        using var app = new BamfApp();
        var probe = app.Services.GetRequiredService<DhcpProbe>();
        probe.Places = Places;
        probe.Exchange = (_, _) => Task.FromResult((new List<DhcpProbe.Heard>(), (string?)"BAMF couldn't listen on UDP port 68 (AccessDenied).", new Dictionary<string, string>()));
        var r = (await probe.RunOnce(CancellationToken.None))!;
        Assert.Contains("port 68", r.Error);
        Assert.All(r.Networks.Where(n => n.SelfIp is not null), n => Assert.Contains("port 68", n.Error));
        Assert.Contains("port 68", probe.Last!.Error);
    }

    [Fact]
    public async Task The_real_sockets_hear_a_local_server_answer_in_its_own_broadcast()
    {
        // A stand-in for a router on this machine: listens where a DHCP server does and answers with the offer, broadcast.
        // Where this machine won't let it have port 67 or 68 (a real DHCP client or server, or no rights), there is nothing to try.
        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            server.EnableBroadcast = true;
            server.Bind(new IPEndPoint(IPAddress.Any, 67));
        }
        catch (SocketException) { return; }

        using var app = new BamfApp();
        var probe = app.Services.GetRequiredService<DhcpProbe>();
        var self = Dns.GetHostEntry(Dns.GetHostName()).AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (self is null) return;
        var question = new DhcpProbe.Question("192.168.77.0/24", self.ToString(), 24, 0x5151, DhcpProbe.BuildDiscover(0x5151));

        var answered = Task.Run(async () =>
        {
            var buf = new byte[1500];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try
            {
                var got = await server.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);
                var r = DhcpProbe.Parse(new byte[] { 2 }.Concat(buf[1..got.ReceivedBytes]).ToArray(), got.ReceivedBytes);       // as a reply, for the xid
                using var send = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
                var offer = Offer(0x5151, "192.168.77.1", "192.168.77.100", gateways: new[] { "192.168.77.1" }, dns: new[] { "192.168.77.1" });
                send.SendTo(offer, new IPEndPoint(IPAddress.Broadcast, 68));
                return r?.Xid;
            }
            catch (OperationCanceledException) { return null; }
        });
        var (heard, error, _) = await probe.RealExchange(new[] { question }, CancellationToken.None);
        await answered;
        if (error is not null) return;                                          // couldn't listen on 68 here: nothing to learn
        var parsed = heard.Select(h => DhcpProbe.Parse(h.Data, h.Data.Length)).Where(x => x is not null).ToList();
        // On a real network the router may answer the question too, and that is none of this test's business. Where local broadcast
        // is filtered nothing of ours arrives; where it does, it must be read as the offer it was.
        var ours = parsed.Where(x => x!.Server == "192.168.77.1").ToList();
        if (ours.Count > 0)
        {
            Assert.Equal("192.168.77.100", ours[0]!.Offered);
            Assert.Equal(new[] { "192.168.77.1" }, ours[0]!.Gateways);
        }
    }

    // ---------- the switches ----------

    [Fact]
    public async Task It_is_on_daily_by_default_and_the_schedule_is_set_and_bounded_through_the_api()
    {
        using var app = new BamfApp();
        var probe = app.Services.GetRequiredService<DhcpProbe>();
        probe.Places = Places;
        probe.Exchange = (_, _) => Task.FromResult((new List<DhcpProbe.Heard>(), (string?)null, new Dictionary<string, string>()));
        var c = app.Client();

        var d = await c.GetFromJsonAsync<JsonElement>("/api/dhcp-probe");
        Assert.True(d.GetProperty("enabled").GetBoolean());
        Assert.Equal(24, d.GetProperty("hours").GetInt32());
        Assert.Equal("02:ba:4d:44:48:43", d.GetProperty("clientMac").GetString());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("last").ValueKind);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/dhcp-probe", new { hours = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/dhcp-probe", new { hours = 500 })).StatusCode);
        var set = await (await c.PostAsJsonAsync("/api/settings/dhcp-probe", new { enabled = true, hours = 6 })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(6, set.GetProperty("hours").GetInt32());
        Assert.Equal(6, probe.Hours);

        // Ask now, and the answer is there; the next is due the schedule's hours after it.
        var run = await (await c.PostAsync("/api/dhcp-probe/run", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Object, run.GetProperty("last").ValueKind);
        var at = DateTime.Parse(run.GetProperty("last").GetProperty("at").GetString()!).ToUniversalTime();
        Assert.Equal(at.AddHours(6), DateTime.Parse(run.GetProperty("nextDue").GetString()!).ToUniversalTime());
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.PostAsync("/api/dhcp-probe/run", null)).StatusCode);       // not twice in ten seconds

        var off = await (await c.PostAsJsonAsync("/api/settings/dhcp-probe", new { enabled = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, off.GetProperty("nextDue").ValueKind);
        Assert.Contains("dhcpProbe", HostStore.TransferKeys);
        Assert.Contains("dhcpProbeHours", HostStore.TransferKeys);
        await Task.CompletedTask;
    }
}
