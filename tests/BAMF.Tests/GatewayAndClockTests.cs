using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>
/// The DHCP check noticing an offer that names a gateway the network's offers never named (learned first, said once, then learned), and the
/// clock check: reading an SNTP reply, saying so when this machine's clock is out by more than allowed, and its switch, server and allowance.
/// </summary>
[Collection("Endpoints")]
public class GatewayAndClockTests
{
    // ------------------------------------------------------------ the gateway an offer names

    private static byte[] Offer(uint xid, string server, string gateway)
    {
        var p = new byte[300];
        p[0] = 2; p[1] = 1; p[2] = 6;
        p[4] = (byte)(xid >> 24); p[5] = (byte)(xid >> 16); p[6] = (byte)(xid >> 8); p[7] = (byte)xid;
        System.Net.IPAddress.Parse("192.168.30.150").GetAddressBytes().CopyTo(p, 16);
        DhcpProbe.ClientMac.CopyTo(p, 28);
        p[236] = 0x63; p[237] = 0x82; p[238] = 0x53; p[239] = 0x63;
        p[240] = 53; p[241] = 1; p[242] = 2;
        p[243] = 54; p[244] = 4; System.Net.IPAddress.Parse(server).GetAddressBytes().CopyTo(p, 245);
        p[249] = 3; p[250] = 4; System.Net.IPAddress.Parse(gateway).GetAddressBytes().CopyTo(p, 251);          // option 3: the router
        p[255] = 255;
        return p;
    }

    private static readonly IReadOnlyDictionary<string, ScannerService.NetworkPlace> Places = new Dictionary<string, ScannerService.NetworkPlace>
    {
        ["192.168.30.0/24"] = new("192.168.30.2", "AA:BB:CC:00:00:02", "192.168.30.1"),
    };

    private sealed class Gw { public string Server = "192.168.30.1"; public string Gateway = "192.168.30.1"; }

    private static DhcpProbe Probe(BamfApp app, Gw g)
    {
        var probe = app.Services.GetRequiredService<DhcpProbe>();
        probe.Places = () => Places;
        probe.Exchange = (qs, _) => Task.FromResult((new List<DhcpProbe.Heard> { new(g.Server, Offer(qs.Single().Xid, g.Server, g.Gateway)) }, (string?)null, new Dictionary<string, string>()));
        return probe;
    }

    private static List<HostStore.AlertRow> Said(BamfApp app) =>
        app.Services.GetRequiredService<HostStore>().GetAlerts().Where(a => a.Title.Contains("different gateway")).ToList();

    [Fact]
    public async Task The_first_gateway_is_learned_and_a_different_one_alerts_once_then_is_learned_too()
    {
        using var app = new BamfApp();
        var g = new Gw();
        var probe = Probe(app, g);
        await probe.RunOnce(CancellationToken.None);
        await probe.RunOnce(CancellationToken.None);
        Assert.Empty(Said(app));                                                    // the same gateway twice: nothing

        g.Server = "192.168.30.99"; g.Gateway = "192.168.30.99";
        await probe.RunOnce(CancellationToken.None);
        var a = Assert.Single(Said(app));
        Assert.Equal("security", a.Kind);
        Assert.Contains("192.168.30.0/24", a.Title);
        Assert.Contains("192.168.30.99", a.Title);
        Assert.Contains("192.168.30.1", a.Detail);                                  // what it used to name

        await probe.RunOnce(CancellationToken.None);
        Assert.Single(Said(app));                                                   // learned: not said again
        g.Gateway = "192.168.30.1";
        await probe.RunOnce(CancellationToken.None);
        Assert.Single(Said(app));                                                   // an old gateway coming back isn't new either
    }

    [Fact]
    public async Task With_the_switch_off_a_new_gateway_is_learned_without_an_alert()
    {
        using var app = new BamfApp();
        var g = new Gw();
        var probe = Probe(app, g);
        await probe.RunOnce(CancellationToken.None);
        var c = app.Client();
        Assert.True((await c.PostAsJsonAsync("/api/settings/dhcp-probe", new { alertOnGateway = false })).IsSuccessStatusCode);
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/dhcp-probe")).GetProperty("alertOnGateway").GetBoolean());
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("editable").GetProperty("dhcpGatewayAlert").GetBoolean());

        g.Gateway = "192.168.30.254";
        await probe.RunOnce(CancellationToken.None);
        Assert.Empty(Said(app));
        await c.PostAsJsonAsync("/api/settings/dhcp-probe", new { alertOnGateway = true });
        await probe.RunOnce(CancellationToken.None);
        Assert.Empty(Said(app));                                                    // it was learned while off
    }

    // ------------------------------------------------------------ the clock

    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Ntp1900 = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static void Stamp(byte[] p, int at, DateTime t)
    {
        var span = t - Ntp1900;
        var secs = (ulong)span.TotalSeconds;
        var frac = (uint)((span.Ticks % TimeSpan.TicksPerSecond) * 4294967296.0 / TimeSpan.TicksPerSecond);
        for (var i = 0; i < 4; i++) { p[at + i] = (byte)(secs >> (24 - 8 * i)); p[at + 4 + i] = (byte)(frac >> (24 - 8 * i)); }
    }

    /// <summary>A server's answer to <paramref name="request"/>, from a server whose clock is <paramref name="serverAhead"/> seconds ahead of the real time <paramref name="sent"/>.</summary>
    private static byte[] Reply(byte[] request, DateTime sent, double serverAhead, double oneWayMs = 10, byte stratum = 2, int leap = 0, bool echo = true)
    {
        var p = new byte[48];
        p[0] = (byte)(leap << 6 | 4 << 3 | 4);                                      // version 4, server
        p[1] = stratum;
        if (echo) Array.Copy(request, 40, p, 24, 8);                               // our transmit time, echoed
        Stamp(p, 32, sent.AddMilliseconds(oneWayMs).AddSeconds(serverAhead));        // received
        Stamp(p, 40, sent.AddMilliseconds(oneWayMs).AddSeconds(serverAhead));        // sent back
        return p;
    }

    private static DateTime ReadStamp(byte[] p, int at)
    {
        ulong secs = 0, frac = 0;
        for (var i = 0; i < 4; i++) { secs = secs << 8 | p[at + i]; frac = frac << 8 | p[at + 4 + i]; }
        return Ntp1900.AddSeconds(secs).AddTicks((long)(frac * (double)TimeSpan.TicksPerSecond / 4294967296.0));
    }

    /// <summary>A server whose clock is <paramref name="serverAhead"/> seconds ahead of the moment each request says it was sent.</summary>
    private static ClockCheck Clock(BamfApp app, double serverAhead)
    {
        var clock = app.Services.GetRequiredService<ClockCheck>();
        clock.Exchange = (server, req, _) => { var sent = ReadStamp(req, 40); return Task.FromResult((Reply(req, sent, serverAhead), sent.AddMilliseconds(20))); };
        return clock;
    }

    [Fact]
    public void A_reply_gives_the_offset_and_round_trip_and_a_reply_that_is_not_an_answer_is_refused()
    {
        var req = ClockCheck.Request(Now);
        Assert.Equal(48, req.Length);
        var ok = ClockCheck.Read(Reply(req, Now, 45), Now, Now.AddMilliseconds(20));
        Assert.NotNull(ok);
        Assert.InRange(ok!.Value.OffsetSeconds, 44.99, 45.01);                      // the server is 45 s ahead: this clock is behind
        Assert.InRange(ok.Value.RoundTripSeconds, 0.019, 0.021);

        var behind = ClockCheck.Read(Reply(req, Now, -3), Now, Now.AddMilliseconds(20));
        Assert.InRange(behind!.Value.OffsetSeconds, -3.01, -2.99);

        Assert.Null(ClockCheck.Read(Reply(req, Now, 0, echo: false), Now, Now.AddMilliseconds(20)));          // didn't echo our time: not an answer to us
        Assert.Null(ClockCheck.Read(Reply(req, Now, 0, stratum: 0), Now, Now.AddMilliseconds(20)));           // "kiss of death"
        Assert.Null(ClockCheck.Read(Reply(req, Now, 0, leap: 3), Now, Now.AddMilliseconds(20)));              // a server that isn't synchronised
        Assert.Null(ClockCheck.Read(new byte[10], Now, Now.AddMilliseconds(20)));
    }

    private static List<HostStore.AlertRow> ClockAlerts(BamfApp app) => app.Services.GetRequiredService<HostStore>().GetAlerts().Where(a => a.Kind == "clock").ToList();

    [Fact]
    public async Task A_clock_within_the_allowance_says_nothing_and_one_outside_alerts_once_a_day()
    {
        using var app = new BamfApp();
        var clock = Clock(app, 4);
        var r = await clock.Check(CancellationToken.None, Now);
        Assert.InRange(r!.OffsetSeconds!.Value, 3.9, 4.1);
        Assert.Null(r.Error);
        Assert.Empty(ClockAlerts(app));                                              // 4 s of 30 allowed

        var late = Clock(app, 95);
        var read = await late.Check(CancellationToken.None, Now);
        Assert.Equal(95, Math.Round(read!.OffsetSeconds!.Value));
        var a = Assert.Single(ClockAlerts(app));
        Assert.Contains("behind", a.Title);
        Assert.Contains("95 seconds", a.Title);
        Assert.Contains("You allow 30 seconds", a.Detail);
        await late.Check(CancellationToken.None, Now.AddHours(1));
        Assert.Single(ClockAlerts(app));                                              // the same day: once
        await late.Check(CancellationToken.None, Now.AddHours(25));
        Assert.Equal(2, ClockAlerts(app).Count);

        Assert.Equal(95, Math.Round(late.Last!.OffsetSeconds!.Value));               // what it last read is kept
    }

    [Fact]
    public async Task A_server_that_does_not_answer_or_answers_nonsense_is_an_error_not_an_alert()
    {
        using var app = new BamfApp();
        var clock = app.Services.GetRequiredService<ClockCheck>();
        clock.Exchange = (_, _, _) => throw new OperationCanceledException();       // a timeout
        var r = await clock.Check(CancellationToken.None, Now);
        Assert.Contains("didn't answer", r!.Error);
        Assert.Null(r.OffsetSeconds);

        clock.Exchange = (_, _, _) => Task.FromResult((new byte[48], Now));
        Assert.Contains("not with a time", (await clock.Check(CancellationToken.None, Now))!.Error);
        Assert.Empty(ClockAlerts(app));
    }

    [Fact]
    public async Task The_server_is_what_was_typed_else_one_dhcp_hands_out_else_the_pool_and_the_switch_asks_straight_away()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var clock = Clock(app, 0);
        Assert.Equal(ClockCheck.DefaultServer, clock.Server);
        Assert.False(clock.Enabled);

        var asked = new List<string>();
        clock.Exchange = (server, req, _) => { asked.Add(server); return Task.FromResult((Reply(req, Now, 0), Now.AddMilliseconds(20))); };
        var on = await c.PostAsJsonAsync("/api/settings/clock-check", new { enabled = true });
        Assert.True(on.IsSuccessStatusCode);
        Assert.Equal(new[] { ClockCheck.DefaultServer }, asked);                     // turning it on asked at once
        Assert.True((await on.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());

        Assert.True((await c.PostAsJsonAsync("/api/settings/clock", new { server = "time.example.org", maxSeconds = 5 })).IsSuccessStatusCode);
        Assert.Equal("time.example.org", clock.Server);
        Assert.Equal(5, clock.MaxSeconds);
        var s = (await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("editable");
        Assert.True(s.GetProperty("clockCheck").GetBoolean());
        Assert.Equal("time.example.org", s.GetProperty("clockServer").GetString());
        Assert.Equal(5, s.GetProperty("clockMaxSeconds").GetInt32());

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/clock", new { server = "not a server!" })).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/clock", new { maxSeconds = 0 })).StatusCode);
        Assert.True((await c.PostAsJsonAsync("/api/settings/clock", new { server = "" })).IsSuccessStatusCode);       // blank: the default again
        Assert.Equal(ClockCheck.DefaultServer, clock.Server);

        var now = await c.PostAsync("/api/clock/run", null);
        Assert.True(now.IsSuccessStatusCode || now.StatusCode == System.Net.HttpStatusCode.TooManyRequests);
        foreach (var k in new[] { "clockCheck", "clockServer", "clockMaxSeconds", "dhcpGatewayAlert" }) Assert.Contains(k, HostStore.TransferKeys);
    }

    [Theory]
    [InlineData("pool.ntp.org", true)]
    [InlineData("192.168.2.1", true)]
    [InlineData("time-a.example.net", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("http://x", false)]
    public void Only_a_name_or_address_is_taken_as_a_server(string s, bool ok) => Assert.Equal(ok, ClockCheck.ValidServer(s));
}
