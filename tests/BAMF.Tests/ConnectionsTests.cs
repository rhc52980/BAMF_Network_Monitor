using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>
/// BAMF's own connections, as the Network services card shows them: that each is there, what state it's
/// in, and that nothing secret is in the answer. And the wider default port scan: its added ports are
/// recorded without an alert the first time a device is scanned for them, and not after.
/// </summary>
[Collection("Endpoints")]
public class ConnectionsTests
{
    private static readonly string[] States = ["ok", "warn", "error", "waiting", "off"];
    private static readonly string[] Sections = ["scanning", "internet", "security", "alerts", "network", "appearance", "system"];

    private static async Task<List<JsonElement>> Items(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/connections")).GetProperty("items").EnumerateArray().ToList();

    private static JsonElement Item(List<JsonElement> items, string id) => items.Single(i => i.GetProperty("id").GetString() == id);
    private static string State(List<JsonElement> items, string id) => Item(items, id).GetProperty("state").GetString()!;

    [Fact]
    public async Task A_new_install_lists_every_connection_with_a_state_and_a_place_in_settings()
    {
        using var app = new BamfApp();
        var items = await Items(app.Client());
        Assert.NotEmpty(items);
        Assert.Equal(items.Count, items.Select(i => i.GetProperty("id").GetString()).Distinct().Count());
        foreach (var i in items)
        {
            Assert.Contains(i.GetProperty("state").GetString(), States);
            Assert.Contains(i.GetProperty("section").GetString(), Sections);
            Assert.Contains(i.GetProperty("group").GetString(), new[] { "Reads from", "Sends to", "Calls out to the internet" });
            Assert.False(string.IsNullOrWhiteSpace(i.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(i.GetProperty("detail").GetString()));
        }
    }

    [Fact]
    public async Task What_isnt_set_up_is_off_and_what_is_on_but_hasnt_run_is_waiting()
    {
        using var app = new BamfApp();
        var items = await Items(app.Client());
        foreach (var id in new[] { "router", "snmp", "remotes", "mdns", "mqtt", "report", "wan", "speed", "greynoise", "update", "webhook" })
            Assert.Equal("off", State(items, id));
        Assert.Equal("waiting", State(items, "backup"));     // on by default, and no night has come yet
        Assert.False(Item(items, "wan").TryGetProperty("at", out var at) && at.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task A_failed_backup_a_failed_speed_test_and_a_clean_greynoise_check_show_as_what_they_are()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var at = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc).ToString("o");
        store.SetSetting("backupLast", JsonSerializer.Serialize(new NightlyBackup.Result(at, null, 0, null, "the disk is full")));
        store.SetSetting("speedTest", "daily");
        store.AddSpeedResult(new HostStore.SpeedResult(at, 0, 0, 0, 0, "", 0, false, "Cloudflare answered HTTP 503"));
        store.SetSetting("greynoise", "true");
        store.SetSetting("greynoiseResult", JsonSerializer.Serialize(new GreyNoiseCheck.Result(at, "203.0.113.9", false, false, null, null, null, "Not observed", null)));

        var items = await Items(app.Client());
        Assert.Equal("error", State(items, "backup"));
        Assert.Contains("the disk is full", Item(items, "backup").GetProperty("detail").GetString());
        Assert.Equal("error", State(items, "speed"));
        Assert.Contains("503", Item(items, "speed").GetProperty("detail").GetString());
        Assert.Equal("ok", State(items, "greynoise"));
        Assert.Equal(at, Item(items, "greynoise").GetProperty("at").GetString());
    }

    [Fact]
    public async Task A_good_speed_test_is_ok_and_says_what_it_measured()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        store.SetSetting("speedTest", "6h");
        store.AddSpeedResult(new HostStore.SpeedResult(DateTime.UtcNow.ToString("o"), 312.4, 35.1, 12, 2, "ATL", 125, false, null));
        var items = await Items(app.Client());
        Assert.Equal("ok", State(items, "speed"));
        Assert.Contains("312 down", Item(items, "speed").GetProperty("detail").GetString());
    }

    [Fact]
    public async Task No_webhook_address_ends_up_in_the_answer()
    {
        using var app = new BamfApp();
        var c = app.Client();
        const string secret = "https://discord.com/api/webhooks/123456789/SECRET-TOKEN-abcdef";
        var set = await c.PostAsJsonAsync("/api/settings/webhook", new { url = secret, format = "discord" });
        Assert.True(set.IsSuccessStatusCode, await set.Content.ReadAsStringAsync());
        var body = await c.GetStringAsync("/api/connections");
        Assert.DoesNotContain("SECRET-TOKEN", body);
        Assert.DoesNotContain("123456789", body);
        var items = await Items(c);
        Assert.Equal("ok", State(items, "dest:main"));
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(999L, "999 B")]
    [InlineData(1_500L, "1.5 KB")]
    [InlineData(2_400_000L, "2.4 MB")]
    [InlineData(3_000_000_000L, "3 GB")]
    public void Sizes_are_said_plainly(long bytes, string text) => Assert.Equal(text, LanWatch.Api.ConnectionsEndpoints.FormatBytes(bytes));

    // ---------- the wider default port scan ----------

    [Fact]
    public void The_added_ports_are_all_in_the_default_set_and_none_is_there_twice()
    {
        var ports = PortChecker.CommonPorts.Select(p => p.Port).ToList();
        Assert.Equal(ports.Count, ports.Distinct().Count());
        Assert.All(PortChecker.AddedPorts, p => Assert.Contains(p, ports));
        Assert.All(PortChecker.CommonPorts, p => Assert.False(string.IsNullOrEmpty(p.Service)));
        Assert.DoesNotContain(22, PortChecker.AddedPorts);      // the original ports aren't "added"
    }

    [Fact]
    public void The_baseline_drops_only_the_added_ports()
    {
        var rows = new[] { 22, 8123, 80, 554 }.Select(p => new HostStore.PortRow(p, "", "", "", true)).ToList();
        Assert.Equal([22, 80], RuleService.BaselineAdded(rows).Select(p => p.Port));
    }

    private static HostRecord NewHost(HostStore store, string mac, string ip)
    {
        store.UpsertSeen(mac, ip, "device-" + ip, "Acme", "192.168.30.0/24");
        return store.GetAll().Single(h => h.Mac == mac);
    }

    private static IEnumerable<PortChecker.PortInfo> Open(params int[] ports) => ports.Select(p => new PortChecker.PortInfo(p, "svc"));
    private static IEnumerable<int> All => PortChecker.CommonPorts.Select(p => p.Port);

    [Fact]
    public async Task A_device_scanned_for_the_added_ports_for_the_first_time_isnt_alerted_about_them_but_is_afterwards()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var rules = app.Services.GetRequiredService<RuleService>();
        var h = NewHost(store, "aa:bb:cc:00:00:01", "192.168.30.50");
        var ports = () => store.GetAlerts(100).Where(a => a.Kind == "port").ToList();

        await rules.RecordScan(h, [80, 443], Open(80), default);                                       // a scan from before the list grew
        Assert.Equal(0, await rules.RecordScan(h, All, Open(80, 8123, 6379), default));                // its first scan for the added ports
        Assert.Empty(ports());
        Assert.Equal("1", store.GetSetting("portBase:" + h.Id));

        Assert.Equal(1, await rules.RecordScan(h, All, Open(80, 8123, 6379, 554), default));           // a real change after that
        var alert = Assert.Single(ports());
        Assert.Contains("554", alert.Title);
        Assert.DoesNotContain("8123", alert.Title);
    }

    [Fact]
    public async Task An_original_port_that_opens_during_that_first_wider_scan_still_alerts()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var rules = app.Services.GetRequiredService<RuleService>();
        var h = NewHost(store, "aa:bb:cc:00:00:02", "192.168.30.51");

        await rules.RecordScan(h, [80, 443, 22], Open(80), default);
        Assert.Equal(1, await rules.RecordScan(h, All, Open(80, 22, 8123), default));
        var alert = Assert.Single(store.GetAlerts(100).Where(a => a.Kind == "port"));
        Assert.Contains("22", alert.Title);
        Assert.DoesNotContain("8123", alert.Title);
    }

    [Fact]
    public async Task A_custom_scan_of_ports_outside_the_added_set_is_unchanged()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var rules = app.Services.GetRequiredService<RuleService>();
        var h = NewHost(store, "aa:bb:cc:00:00:03", "192.168.30.52");

        await rules.RecordScan(h, [22, 80], Open(80), default);
        Assert.Equal(1, await rules.RecordScan(h, [22, 80], Open(22, 80), default));
        Assert.Null(store.GetSetting("portBase:" + h.Id));
    }
}
