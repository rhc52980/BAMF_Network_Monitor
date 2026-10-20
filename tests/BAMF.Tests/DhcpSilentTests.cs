using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>
/// The DHCP check noticing a network that had a server and no longer does: two silent checks in a row before it says so, once, and again
/// when a server answers; checks that couldn't ask change nothing; and its switch.
/// </summary>
[Collection("Endpoints")]
public class DhcpSilentTests
{
    private static byte[] Offer(uint xid, string server)
    {
        var p = new byte[300];
        p[0] = 2; p[1] = 1; p[2] = 6;
        p[4] = (byte)(xid >> 24); p[5] = (byte)(xid >> 16); p[6] = (byte)(xid >> 8); p[7] = (byte)xid;
        System.Net.IPAddress.Parse("192.168.30.150").GetAddressBytes().CopyTo(p, 16);
        DhcpProbe.ClientMac.CopyTo(p, 28);
        p[236] = 0x63; p[237] = 0x82; p[238] = 0x53; p[239] = 0x63;
        p[240] = 53; p[241] = 1; p[242] = 2;
        p[243] = 54; p[244] = 4; System.Net.IPAddress.Parse(server).GetAddressBytes().CopyTo(p, 245);
        p[249] = 255;
        return p;
    }

    private static readonly IReadOnlyDictionary<string, ScannerService.NetworkPlace> Places = new Dictionary<string, ScannerService.NetworkPlace>
    {
        ["192.168.30.0/24"] = new("192.168.30.2", "AA:BB:CC:00:00:02", "192.168.30.1"),
    };

    /// <summary>A probe whose network answers or doesn't, as the test says; a null from <paramref name="listen"/> is a round that couldn't listen.</summary>
    private sealed class Net
    {
        public bool Answers = true;
        public string? ListenError;
    }

    private static DhcpProbe Probe(BamfApp app, Net net)
    {
        var probe = app.Services.GetRequiredService<DhcpProbe>();
        probe.Places = () => Places;
        probe.Exchange = (qs, _) =>
        {
            var heard = new List<DhcpProbe.Heard>();
            if (net.Answers && net.ListenError is null) heard.Add(new("192.168.30.1", Offer(qs.Single().Xid, "192.168.30.1")));
            return Task.FromResult((heard, net.ListenError, new Dictionary<string, string>()));
        };
        return probe;
    }

    private static List<TrafficMonitor.Alert> Dhcp(BamfApp app) => app.Services.GetRequiredService<TrafficMonitor>().Alerts().Where(a => a.Kind == "dhcp").ToList();

    [Fact]
    public async Task Two_silent_checks_in_a_row_after_an_answer_alert_once_and_the_next_answer_says_it_is_back()
    {
        using var app = new BamfApp();
        var net = new Net();
        var probe = Probe(app, net);
        await probe.RunOnce(CancellationToken.None);
        Assert.Empty(Dhcp(app));                                              // an answer: the first server is learned quietly

        net.Answers = false;
        await probe.RunOnce(CancellationToken.None);
        Assert.Empty(Dhcp(app));                                              // one silent check is a hiccup
        await probe.RunOnce(CancellationToken.None);
        var alert = Assert.Single(Dhcp(app));
        Assert.Contains("192.168.30.0/24", alert.Title);
        Assert.Contains("No DHCP server is answering", alert.Title);

        await probe.RunOnce(CancellationToken.None);
        await probe.RunOnce(CancellationToken.None);
        Assert.Single(Dhcp(app));                                             // still silent: not said again

        net.Answers = true;
        await probe.RunOnce(CancellationToken.None);
        Assert.Equal(2, Dhcp(app).Count);
        Assert.Contains("answering again", Dhcp(app)[0].Title);
    }

    [Fact]
    public async Task A_silent_check_brings_the_next_one_forward_and_an_answer_puts_it_back()
    {
        using var app = new BamfApp();
        var net = new Net();
        var probe = Probe(app, net);
        await probe.RunOnce(CancellationToken.None);
        var usual = probe.NextDue()!.Value - DateTime.UtcNow;
        Assert.True(usual > TimeSpan.FromHours(23));

        net.Answers = false;
        await probe.RunOnce(CancellationToken.None);
        var soon = probe.NextDue()!.Value - DateTime.UtcNow;
        Assert.True(soon < TimeSpan.FromMinutes(11), soon.ToString());

        await probe.RunOnce(CancellationToken.None);                          // the second silence alerts; no more rechecks to make
        Assert.True(probe.NextDue()!.Value - DateTime.UtcNow > TimeSpan.FromHours(23));
    }

    [Fact]
    public async Task A_network_that_never_answered_or_a_check_that_couldnt_ask_alerts_nothing()
    {
        using var app = new BamfApp();
        var net = new Net { Answers = false };
        var probe = Probe(app, net);
        for (var i = 0; i < 4; i++) await probe.RunOnce(CancellationToken.None);
        Assert.Empty(Dhcp(app));                                              // nobody ever answered: no DHCP server there, or none BAMF can hear

        net.Answers = true;
        await probe.RunOnce(CancellationToken.None);
        net.Answers = false; net.ListenError = "port 68 is in use";
        for (var i = 0; i < 4; i++) await probe.RunOnce(CancellationToken.None);
        Assert.Empty(Dhcp(app));                                              // not being able to listen says nothing about the server
        net.ListenError = null;
        await probe.RunOnce(CancellationToken.None);
        Assert.Empty(Dhcp(app));                                              // and the silence count started from zero
    }

    [Fact]
    public async Task Switched_off_it_says_nothing_and_the_setting_is_in_the_api_and_the_settings_file()
    {
        using var app = new BamfApp();
        var net = new Net();
        var probe = Probe(app, net);
        var http = app.CreateClient();
        var set = await http.PostAsJsonAsync("/api/settings/dhcp-probe", new { alertOnSilence = false });
        Assert.True(set.IsSuccessStatusCode);
        var got = await http.GetFromJsonAsync<JsonElement>("/api/dhcp-probe");
        Assert.False(got.GetProperty("alertOnSilence").GetBoolean());
        Assert.Contains("dhcpSilentAlert", HostStore.TransferKeys);

        await probe.RunOnce(CancellationToken.None);
        net.Answers = false;
        for (var i = 0; i < 4; i++) await probe.RunOnce(CancellationToken.None);
        Assert.Empty(Dhcp(app));
    }
}
