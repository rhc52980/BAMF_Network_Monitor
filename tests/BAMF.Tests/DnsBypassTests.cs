using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BAMF.Tests;

/// <summary>
/// Devices that ask a DNS server other than the ones the network hands out: nothing is judged until the DHCP check has said what those are, a
/// device on the network (a Pi-hole) counts as the network's own, the once-per-device alert is off unless switched on, what is known survives a
/// restart, and the card's data and switches.
/// </summary>
[Collection("Endpoints")]
public class DnsBypassTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bamf-bypass-" + Guid.NewGuid().ToString("N"));
    public DnsBypassTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private HostStore Store() =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bamf:DatabasePath"] = Path.Combine(_dir, "bamf.db") }).Build());
    private static TrafficMonitor Monitor(HostStore store) => new(NullLogger<TrafficMonitor>.Instance, store);

    private const string Tv = "aa:bb:cc:00:00:01", Laptop = "aa:bb:cc:00:00:02";

    [Fact]
    public void Nothing_is_judged_until_the_network_has_said_what_its_own_dns_is()
    {
        var t = Monitor(Store());
        t.Dns(Tv, "8.8.8.8");
        var r = t.DnsBypassers();
        Assert.False(r.Known);
        Assert.Empty(r.Devices);
    }

    [Fact]
    public void A_device_asking_a_server_the_network_does_not_hand_out_is_listed_with_how_often()
    {
        var store = Store();
        var t = Monitor(store);
        t.SetOwnDns(new[] { "192.168.2.1" });
        t.Dns(Laptop, "192.168.2.1");
        t.Dns(Laptop, "192.168.2.1");
        for (var i = 0; i < 3; i++) t.Dns(Tv, "8.8.8.8");
        t.Dns(Tv, "192.168.2.1");

        var r = t.DnsBypassers();
        Assert.True(r.Known);
        Assert.Equal(new[] { "192.168.2.1" }, r.Own);
        var d = Assert.Single(r.Devices);
        Assert.Equal(Tv, d.Mac);
        var s = Assert.Single(d.Servers);
        Assert.Equal("8.8.8.8", s.Ip);
        Assert.Equal(3, s.Queries);
    }

    [Fact]
    public void A_device_on_the_network_a_gateway_and_addresses_that_are_not_routable_are_not_skipping_anything()
    {
        var store = Store();
        store.UpsertSeen("aa:bb:cc:00:00:53", "192.168.2.53", "pihole", "Raspberry Pi", "192.168.2.0/24");      // a Pi-hole the DHCP didn't mention
        var t = Monitor(store);
        t.SetOwnDns(new[] { "192.168.2.1" });
        t.Dns(Tv, "192.168.2.53");
        t.Dns(Tv, "127.0.0.1");
        t.Dns(Tv, "169.254.1.1");
        t.Dns(Tv, "224.0.0.251");
        t.Dns(Tv, "2001:4860:4860::8888");                                                                         // IPv6 isn't judged
        Assert.Empty(t.DnsBypassers().Devices);
    }

    [Fact]
    public void When_dhcp_itself_hands_out_a_public_server_asking_it_is_not_skipping()
    {
        var t = Monitor(Store());
        t.SetOwnDns(new[] { "192.168.2.1", "1.1.1.1" });
        t.Dns(Tv, "1.1.1.1");
        Assert.Empty(t.DnsBypassers().Devices);
        t.Dns(Tv, "8.8.4.4");
        Assert.Equal("8.8.4.4", Assert.Single(Assert.Single(t.DnsBypassers().Devices).Servers).Ip);
    }

    [Fact]
    public void The_alert_is_off_until_switched_on_then_comes_once_per_device_in_place_of_the_plainer_ones()
    {
        var store = Store();
        var t = Monitor(store);
        t.SetOwnDns(new[] { "192.168.2.1" });
        t.Dns(Tv, "192.168.2.1");
        t.Dns(Tv, "8.8.8.8");                                                                                      // off: only the old "changed DNS server" alert
        Assert.DoesNotContain(t.Alerts(), a => a.Title.Contains("skips"));

        store.SetSetting("dnsBypassAlert", "true");
        Assert.True(t.DnsBypassAlertEnabled);
        t.Dns(Laptop, "192.168.2.1");
        t.Dns(Laptop, "9.9.9.9");
        var a = Assert.Single(t.Alerts(), x => x.Title.Contains("skips"));
        Assert.Equal("dns", a.Kind);
        Assert.Contains("9.9.9.9", a.Detail);
        Assert.Contains("192.168.2.1", a.Detail);
        Assert.DoesNotContain(t.Alerts(), x => x.Title.Contains("changed DNS server") && x.Detail.Contains("9.9.9.9"));

        t.Dns(Laptop, "9.9.9.9");                                                                                  // the same again: nothing more
        Assert.Single(t.Alerts(), x => x.Title.Contains("skips"));
    }

    [Fact]
    public void An_address_the_network_does_hand_out_never_alerts_as_skipping()
    {
        var store = Store();
        store.SetSetting("dnsBypassAlert", "true");
        var t = Monitor(store);
        t.SetOwnDns(new[] { "192.168.2.1" });
        t.Dns(Laptop, "192.168.2.1");
        Assert.DoesNotContain(t.Alerts(), a => a.Title.Contains("skips"));
    }

    [Fact]
    public void What_the_network_hands_out_and_what_was_heard_are_kept_across_a_restart()
    {
        var store = Store();
        var first = Monitor(store);
        first.SetOwnDns(new[] { "192.168.2.1" });
        first.Dns(Tv, "8.8.8.8");
        first.FlushHourly();

        var second = Monitor(store);
        var r = second.DnsBypassers();
        Assert.True(r.Known);
        Assert.Equal(Tv, Assert.Single(r.Devices).Mac);
    }

    [Fact]
    public async Task The_traffic_answer_carries_the_list_and_both_switches_are_saved()
    {
        using var app = new BamfApp();
        var scanner = app.Services.GetRequiredService<ScannerService>();
        var store = app.Services.GetRequiredService<HostStore>();
        store.UpsertSeen(Tv, "192.168.2.40", "living-room-tv", "Samsung", "192.168.2.0/24");
        scanner.Traffic.SetOwnDns(new[] { "192.168.2.1" });
        scanner.Traffic.Dns(Tv, "8.8.8.8");
        var c = app.Client();

        var t = await c.GetFromJsonAsync<JsonElement>("/api/traffic");
        var b = t.GetProperty("dnsBypass");
        Assert.True(b.GetProperty("enabled").GetBoolean());
        Assert.False(b.GetProperty("alert").GetBoolean());
        Assert.True(b.GetProperty("known").GetBoolean());
        var d = Assert.Single(b.GetProperty("devices").EnumerateArray());
        Assert.Equal("living-room-tv", d.GetProperty("name").GetString());
        Assert.Equal("8.8.8.8", d.GetProperty("servers")[0].GetProperty("ip").GetString());

        Assert.True((await c.PostAsJsonAsync("/api/settings/dns-bypass-alert", new { enabled = true })).IsSuccessStatusCode);
        Assert.True((await c.PostAsJsonAsync("/api/settings/dns-bypass-watch", new { enabled = false })).IsSuccessStatusCode);
        var s = (await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("editable");
        Assert.True(s.GetProperty("dnsBypassAlert").GetBoolean());
        Assert.False(s.GetProperty("dnsBypassWatch").GetBoolean());

        var off = (await c.GetFromJsonAsync<JsonElement>("/api/traffic")).GetProperty("dnsBypass");
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, off.GetProperty("devices").ValueKind);
    }
}
