using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BAMF.Tests;

/// <summary>
/// The router advertisement watch: an IPv6 router heard for the first time alerts, the first ones are learned quietly, an advertisement
/// that says "don't use me" is ignored, trusting and forgetting, what is kept across a restart, the switch, and the card's data.
/// Made-up frames fed to the traffic monitor.
/// </summary>
[Collection("Endpoints")]
public class RouterAdvertTests
{
    /// <summary>An Ethernet frame carrying an IPv6 router advertisement from the given MAC and link-local address.</summary>
    private static byte[] Frame(string mac, string ip, int lifetime = 1800, int type = 134)
    {
        var d = new byte[14 + 40 + 16];
        for (var i = 0; i < 6; i++) { d[i] = (i == 0 ? (byte)0x33 : (byte)0x00); d[6 + i] = Convert.ToByte(mac.Split(':')[i], 16); }
        d[0] = 0x33; d[1] = 0x33; d[5] = 0x01;                                  // to all nodes
        d[12] = 0x86; d[13] = 0xDD;
        d[14] = 0x60;
        d[14 + 4] = 0; d[14 + 5] = 16;                                          // payload length
        d[14 + 6] = 58; d[14 + 7] = 255;                                        // ICMPv6, hop limit 255
        System.Net.IPAddress.Parse(ip).GetAddressBytes().CopyTo(d, 14 + 8);
        System.Net.IPAddress.Parse("ff02::1").GetAddressBytes().CopyTo(d, 14 + 24);
        d[14 + 40] = (byte)type;
        d[14 + 46] = (byte)(lifetime >> 8); d[14 + 47] = (byte)lifetime;
        return d;
    }

    private static List<TrafficMonitor.Alert> Ra(TrafficMonitor t) => t.Alerts().Where(a => a.Kind == "ra").ToList();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bamf-ra-" + Guid.NewGuid().ToString("N"));
    private HostStore Store()
    {
        Directory.CreateDirectory(_dir);
        return new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bamf:DatabasePath"] = Path.Combine(_dir, "bamf.db") }).Build());
    }
    private static TrafficMonitor Monitor(HostStore store) => new(NullLogger<TrafficMonitor>.Instance, store);

    [Fact]
    public void The_first_router_is_learned_quietly_and_a_second_one_alerts_once_with_its_address()
    {
        var store = Store();
        var t = Monitor(store);
        t.Ingest(Frame("AA:BB:CC:00:00:01", "fe80::1"));
        Assert.Empty(Ra(t));
        var r = Assert.Single(t.RouterAdverts());
        Assert.Equal("fe80::1", r.Ip);
        Assert.Equal(1800, r.LifetimeSeconds);
        Assert.Contains("AA:BB:CC:00:00:01", t.KnownRa);

        t.Ingest(Frame("AA:BB:CC:00:00:01", "fe80::1"));
        Assert.Empty(Ra(t));                                                    // the same router again

        t.Ingest(Frame("AA:BB:CC:00:00:66", "fe80::66"));
        var alert = Assert.Single(Ra(t));
        Assert.Contains("fe80::66", alert.Title);
        Assert.Contains("AA:BB:CC:00:00:66", alert.Detail);
        t.Ingest(Frame("AA:BB:CC:00:00:66", "fe80::66"));
        Assert.Single(Ra(t));                                                   // told once; now known
        Assert.Equal(2, t.RouterAdverts().Count);
        Assert.Equal(2, t.RouterAdverts().Single(x => x.Mac == "AA:BB:CC:00:00:66").Adverts);
    }

    [Fact]
    public void An_advertisement_with_no_lifetime_or_another_icmp_message_is_nothing()
    {
        var t = Monitor(Store());
        t.Ingest(Frame("AA:BB:CC:00:00:01", "fe80::1"));
        t.Ingest(Frame("AA:BB:CC:00:00:77", "fe80::77", lifetime: 0));          // "don't use me as a router"
        t.Ingest(Frame("AA:BB:CC:00:00:78", "fe80::78", type: 135));            // a neighbour solicitation
        Assert.Empty(Ra(t));
        Assert.Equal("fe80::1", Assert.Single(t.RouterAdverts()).Ip);
    }

    [Fact]
    public void A_trusted_router_never_alerts_and_a_forgotten_one_does_again()
    {
        var t = Monitor(Store());
        t.Ingest(Frame("AA:BB:CC:00:00:01", "fe80::1"));                         // learned
        t.Trust("ra", "AA:BB:CC:00:00:02", true);
        t.Ingest(Frame("AA:BB:CC:00:00:02", "fe80::2"));
        Assert.Empty(Ra(t));

        t.Trust("ra", "aa:bb:cc:00:00:02", false);                               // the MAC's case doesn't matter
        t.Ingest(Frame("AA:BB:CC:00:00:02", "fe80::2"));
        Assert.Contains("fe80::2", Assert.Single(Ra(t)).Title);
    }

    [Fact]
    public void Routers_and_the_trusted_set_survive_a_restart_so_a_known_one_doesnt_alert_again()
    {
        var store = Store();
        var first = Monitor(store);
        first.Ingest(Frame("AA:BB:CC:00:00:01", "fe80::1"));
        first.Ingest(Frame("AA:BB:CC:00:00:02", "fe80::2"));                    // alerts, and is known from now on
        first.FlushHourly();

        var second = Monitor(store);
        Assert.Equal(2, second.RouterAdverts().Count);
        Assert.Contains("AA:BB:CC:00:00:02", second.KnownRa);
        second.Ingest(Frame("AA:BB:CC:00:00:02", "fe80::2"));
        Assert.Single(Ra(second));                                      // only the one alert the first run raised, which was kept
    }

    [Fact]
    public async Task The_switch_stops_the_watch_and_the_card_data_and_trusting_by_mac_work_through_the_api()
    {
        using var app = new BamfApp();
        var t = app.Services.GetRequiredService<TrafficMonitor>();
        var http = app.CreateClient();
        t.Ingest(Frame("AA:BB:CC:00:00:01", "fe80::1"));

        var traffic = await http.GetFromJsonAsync<JsonElement>("/api/traffic");
        var row = traffic.GetProperty("ra").EnumerateArray().Single();
        Assert.Equal("fe80::1", row.GetProperty("ip").GetString());
        Assert.Equal("AA:BB:CC:00:00:01", row.GetProperty("mac").GetString());
        Assert.True(row.GetProperty("trusted").GetBoolean());                   // the first is learned, so trusted

        var trust = await http.PostAsJsonAsync("/api/traffic/trust", new { kind = "ra", ip = "AA:BB:CC:00:00:01", trusted = false });
        Assert.True(trust.IsSuccessStatusCode);
        Assert.DoesNotContain("AA:BB:CC:00:00:01", t.KnownRa);
        var bad = await http.PostAsJsonAsync("/api/traffic/trust", new { kind = "ra", ip = "not a mac", trusted = true });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);

        var off = await http.PostAsJsonAsync("/api/settings/ra-watch", new { enabled = false });
        Assert.True(off.IsSuccessStatusCode);
        t.Ingest(Frame("AA:BB:CC:00:00:09", "fe80::9"));
        Assert.Empty(Ra(t));
        Assert.DoesNotContain(t.RouterAdverts(), x => x.Mac == "AA:BB:CC:00:00:09");
        var settings = await http.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.False(settings.GetProperty("editable").GetProperty("routerAdvertWatch").GetBoolean());
        Assert.Contains("routerAdvertWatch", HostStore.TransferKeys);
    }
}
