using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LanWatch.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>The cutover check, the segmentation check and DHCP pool pressure.</summary>
[Collection("Endpoints")]
public class ChecksTests
{
    private const string Net = "192.168.30.0/24", Iot = "192.168.20.0/24";

    private static HostStore Store(BamfApp app) => app.Services.GetRequiredService<HostStore>();

    private static void Seen(HostStore store, string mac, string ip, string name, string net = Net) => store.UpsertSeen(mac, ip, name, "Acme", net);

    private static HostRecord Host(HostStore store, string mac) => store.GetAll().First(h => h.Mac == mac);

    /// <summary>Changes a device the way the network changes it, which a scan's own upsert won't do for a device still answering.</summary>
    private static void Sql(HostStore store, string sql, params (string, object)[] args)
    {
        using var db = new SqliteConnection($"Data Source={store.DatabasePath}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- the cutover check

    [Fact]
    public async Task A_snapshot_is_compared_with_now_by_mac_so_a_device_at_a_new_address_is_moved_not_missing()
    {
        using var app = new BamfApp();
        var store = Store(app);
        var c = app.Client();
        Seen(store, "AA:00:00:00:00:01", "192.168.30.10", "pc");
        Seen(store, "AA:00:00:00:00:02", "192.168.30.11", "tv");
        Seen(store, "AA:00:00:00:00:03", "192.168.30.12", "camera");
        Seen(store, "AA:00:00:00:00:04", "192.168.30.13", "phone");
        var made = await c.PostAsJsonAsync("/api/cutover", new { name = "Before the new gateway" });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var id = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        // The change: the TV comes back at another address, the camera doesn't come back, the phone is a new device with the same name
        // (a private Wi-Fi address on the new network), and something nobody had before appears.
        Sql(store, "UPDATE hosts SET ip = '192.168.10.50', subnet = '192.168.10.0/24' WHERE mac = 'AA:00:00:00:00:02'");
        Sql(store, "UPDATE hosts SET online = 0 WHERE mac IN ('AA:00:00:00:00:03', 'AA:00:00:00:00:04')");
        Seen(store, "AA:00:00:00:00:14", "192.168.30.60", "phone");
        Seen(store, "AA:00:00:00:00:15", "192.168.30.61", "new-thing");

        var d = await c.GetFromJsonAsync<JsonElement>($"/api/cutover/{id}/compare");
        Assert.Equal((4, 0), (d.GetProperty("wasOnline").GetInt32(), d.GetProperty("wasOffline").GetInt32()));
        string[] Names(string key) => d.GetProperty(key).EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(new[] { "pc" }, Names("back"));
        Assert.Equal(new[] { "tv" }, Names("moved"));
        var tv = d.GetProperty("moved").EnumerateArray().Single();
        Assert.Equal(("192.168.10.50", "192.168.30.11", "192.168.10.0/24"), (tv.GetProperty("ip").GetString(), tv.GetProperty("wasIp").GetString(), tv.GetProperty("network").GetString()));
        Assert.Equal(new[] { "camera" }, Names("missing"));
        Assert.Equal(new[] { "new-thing" }, Names("added"));
        var pair = d.GetProperty("sameName").EnumerateArray().Single();                 // the old phone and the new one are very likely one device
        Assert.Equal(("AA:00:00:00:00:04", "AA:00:00:00:00:14"), (pair.GetProperty("was").GetProperty("mac").GetString(), pair.GetProperty("now").GetProperty("mac").GetString()));
    }

    [Fact]
    public async Task Devices_that_were_off_before_are_not_expected_back_and_ones_you_ignore_are_left_out()
    {
        using var app = new BamfApp();
        var store = Store(app);
        var c = app.Client();
        Seen(store, "AA:00:00:00:00:01", "192.168.30.10", "pc");
        Seen(store, "AA:00:00:00:00:02", "192.168.30.11", "spare");
        Seen(store, "AA:00:00:00:00:03", "192.168.30.12", "noisy");
        Sql(store, "UPDATE hosts SET online = 0 WHERE mac = 'AA:00:00:00:00:02'");     // off when the snapshot was taken
        var id = (await (await c.PostAsJsonAsync("/api/cutover", new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        Sql(store, "UPDATE hosts SET online = 0, ignored = 1 WHERE mac = 'AA:00:00:00:00:03'");
        Sql(store, "UPDATE hosts SET online = 0 WHERE mac = 'AA:00:00:00:00:01'");

        var d = await c.GetFromJsonAsync<JsonElement>($"/api/cutover/{id}/compare");
        Assert.Equal((2, 1), (d.GetProperty("wasOnline").GetInt32(), d.GetProperty("wasOffline").GetInt32()));
        Assert.Equal(new[] { "pc" }, d.GetProperty("missing").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToArray());   // not the spare, not the ignored one
    }

    [Fact]
    public async Task Only_the_newest_five_snapshots_are_kept_and_one_can_be_deleted()
    {
        using var app = new BamfApp();
        var store = Store(app);
        var c = app.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/cutover", new { })).StatusCode);   // nothing to keep yet
        Seen(store, "AA:00:00:00:00:01", "192.168.30.10", "pc");
        for (var i = 1; i <= 6; i++) await c.PostAsJsonAsync("/api/cutover", new { name = "Snapshot " + i });
        var list = (await c.GetFromJsonAsync<JsonElement>("/api/cutover")).GetProperty("snapshots").EnumerateArray().ToList();
        Assert.Equal(new[] { "Snapshot 6", "Snapshot 5", "Snapshot 4", "Snapshot 3", "Snapshot 2" }, list.Select(s => s.GetProperty("name").GetString()).ToArray());
        var id = list[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"/api/cutover/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/cutover/{id}/compare")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/cutover/{id}")).StatusCode);
    }

    // ---------------------------------------------------------------- the segmentation check

    private static SegmentationCheck.Rule Rule(string label, string where = "on", string network = Iot, string tag = "", string type = "", params string[] words) =>
        new("r-" + label, label, tag, words.ToList(), type, where, network);

    [Fact]
    public void A_rule_picks_devices_by_tag_type_or_words_and_says_what_it_matched()
    {
        var h = new HostRecord(1, "AA:00:00:00:00:01", "192.168.30.5", "garage-cam", "", "Axis Communications", Net, true, false, false, false, false, "", "", "", "IP camera (554/80)", "", "", "");
        Assert.True(SegmentationCheck.Matches(Rule("Cameras", words: new[] { "camera" }), h, null, null, null, out var why));
        Assert.Equal("named like \"camera\"", why);                                                              // BAMF's guess says camera
        Assert.True(SegmentationCheck.Matches(Rule("Cameras", words: new[] { "cam" }), h, null, null, null, out _));          // and so does the name: a word, not part of one
        Assert.False(SegmentationCheck.Matches(Rule("Cameras", words: new[] { "ca" }), h, null, null, null, out _));          // "ca" is inside a word, not a word
        Assert.True(SegmentationCheck.Matches(Rule("Kids", tag: "kids"), h, new[] { "Kids" }, null, null, out why));
        Assert.Equal("tagged kids", why);
        Assert.True(SegmentationCheck.Matches(Rule("Plugs", type: "plug"), h, null, "plug", null, out _));
        Assert.True(SegmentationCheck.Matches(Rule("Plugs", type: "Smart plug"), h, null, null, "smart plug", out _));          // the type you wrote for it
        Assert.False(SegmentationCheck.Matches(Rule("TVs", words: new[] { "tv", "roku" }), h, null, null, null, out _));
    }

    [Fact]
    public void A_rule_has_to_say_what_it_picks_where_and_for_which_network()
    {
        Assert.Null(SegmentationCheck.Clean(new[] { Rule("") }).Rules);
        Assert.Null(SegmentationCheck.Clean(new[] { Rule("x", words: Array.Empty<string>()) }).Rules);                // nothing to pick devices by
        Assert.Null(SegmentationCheck.Clean(new[] { Rule("x", where: "sideways", words: new[] { "cam" }) }).Rules);
        Assert.Null(SegmentationCheck.Clean(new[] { Rule("x", network: "", words: new[] { "cam" }) }).Rules);
        Assert.Null(SegmentationCheck.Clean(new[] { Rule("x", words: new[] { new string('w', 31) }) }).Rules);
        var (rules, error) = SegmentationCheck.Clean(new[] { Rule("Cameras", words: new[] { " cam ", "Cam", "doorbell" }) with { Id = "" } });
        Assert.Null(error);
        Assert.Equal(new[] { "cam", "doorbell" }, rules![0].Words);                                                  // trimmed, once each
        Assert.NotEqual("", rules[0].Id);
    }

    private static async Task<JsonElement> Seg(HttpClient c, object body) => await (await c.PostAsJsonAsync("/api/settings/segmentation", body)).Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Devices_on_the_wrong_network_are_listed_once_it_is_switched_on_and_not_before()
    {
        using var app = new BamfApp();
        var store = Store(app);
        var c = app.Client();
        Seen(store, "AA:00:00:00:00:01", "192.168.30.5", "garage-cam");                          // a camera on the main network
        Seen(store, "AA:00:00:00:00:02", "192.168.20.5", "porch-camera", Iot);                    // a camera where it belongs
        Seen(store, "AA:00:00:00:00:03", "192.168.20.6", "smart-plug-1", Iot);                    // a plug where it must never be (below)
        var rules = new[] { new { label = "Cameras", tag = "", words = new[] { "cam", "camera" }, type = "", where = "on", network = Iot },
                            new { label = "Plugs", tag = "", words = new[] { "plug" }, type = "", where = "off", network = Iot } };
        var saved = await Seg(c, new { rules });
        Assert.False(saved.GetProperty("enabled").GetBoolean());
        Assert.Empty(saved.GetProperty("findings").EnumerateArray());                              // rules are kept, nothing is looked at

        var on = await Seg(c, new { enabled = true });
        var f = on.GetProperty("findings").EnumerateArray().ToList();
        Assert.Equal(new[] { ("Cameras", "garage-cam", Net, Iot, "on"), ("Plugs", "smart-plug-1", Iot, Iot, "off") },
            f.Select(x => (x.GetProperty("rule").GetString()!, x.GetProperty("name").GetString()!, x.GetProperty("network").GetString()!, x.GetProperty("expected").GetString()!, x.GetProperty("where").GetString()!)).ToArray());
        Assert.Contains(Net, on.GetProperty("networks").EnumerateArray().Select(n => n.GetString()));
    }

    [Fact]
    public async Task A_device_that_newly_breaks_a_rule_raises_one_alert_and_what_was_already_so_does_not()
    {
        using var app = new BamfApp();
        var store = Store(app);
        var seg = app.Services.GetRequiredService<SegmentationCheck>();
        var c = app.Client();
        Seen(store, "AA:00:00:00:00:01", "192.168.30.5", "garage-cam");                          // already wrong when the alert is switched on
        await Seg(c, new { rules = new[] { new { label = "Cameras", tag = "", words = new[] { "cam" }, type = "", where = "on", network = Iot } }, enabled = true });
        await Seg(c, new { alert = true });
        await seg.Run(default);
        Assert.Empty(store.GetAlerts(50));                                                        // it's in the list, not an alert

        Seen(store, "AA:00:00:00:00:02", "192.168.30.6", "porch-cam");
        await seg.Run(default);
        var alert = Assert.Single(store.GetAlerts(50));
        Assert.Equal(("security", "porch-cam is on 192.168.30.0/24, outside your \"Cameras\" rule"), (alert.Kind, alert.Title));
        await seg.Run(default);
        Assert.Single(store.GetAlerts(50));                                                       // not again while it stays so

        // Put right, then wrong again: that is news again.
        Sql(store, "UPDATE hosts SET subnet = '192.168.20.0/24' WHERE mac = 'AA:00:00:00:00:02'");
        await seg.Run(default);
        Sql(store, "UPDATE hosts SET subnet = '192.168.30.0/24' WHERE mac = 'AA:00:00:00:00:02'");
        await seg.Run(default);
        Assert.Equal(2, store.GetAlerts(50).Count);
    }

    [Fact]
    public async Task Many_new_findings_at_once_are_one_alert_and_the_alert_is_off_unless_switched_on()
    {
        using var app = new BamfApp();
        var store = Store(app);
        var seg = app.Services.GetRequiredService<SegmentationCheck>();
        var c = app.Client();
        await Seg(c, new { rules = new[] { new { label = "Cameras", tag = "", words = new[] { "cam" }, type = "", where = "on", network = Iot } }, enabled = true });
        for (var i = 1; i <= 5; i++) Seen(store, $"AA:00:00:00:00:0{i}", $"192.168.30.{i}", $"cam-{i}");
        await seg.Run(default);
        Assert.Empty(store.GetAlerts(50));                                                        // the alert switch is off
        await Seg(c, new { alert = true });                                                       // baseline: these five are already so
        Seen(store, "AA:00:00:00:00:10", "192.168.30.10", "cam-a"); Seen(store, "AA:00:00:00:00:11", "192.168.30.11", "cam-b");
        Seen(store, "AA:00:00:00:00:12", "192.168.30.12", "cam-c"); Seen(store, "AA:00:00:00:00:13", "192.168.30.13", "cam-d");
        await seg.Run(default);
        Assert.Equal("4 devices are on the wrong network", Assert.Single(store.GetAlerts(50)).Title);
    }

    [Fact]
    public async Task A_rule_with_nothing_to_pick_devices_by_is_refused_and_nothing_is_saved()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var res = await c.PostAsJsonAsync("/api/settings/segmentation", new { rules = new[] { new { label = "Empty", tag = "", words = Array.Empty<string>(), type = "", where = "on", network = Iot } } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty((await c.GetFromJsonAsync<JsonElement>("/api/segmentation")).GetProperty("rules").EnumerateArray());
    }

    // ---------------------------------------------------------------- DHCP pool pressure

    private static PoolWatch.Pool Pool(string start = "192.168.30.100", string end = "192.168.30.109", string net = Net, string name = "Main") => new(net, name, start, end, "manual");

    private static HostRecord H(string ip, bool online, string lastSeen = "", bool ignored = false) =>
        new(1, "AA:00:00:00:00:" + ip.Split('.')[3].PadLeft(2, '0'), ip, "", "", "", Net, online, false, ignored, false, false, "", "", lastSeen, "", "", "", "");

    [Fact]
    public void A_pool_counts_the_devices_seen_at_an_address_in_its_range_in_the_last_day()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var hosts = new[]
        {
            H("192.168.30.100", true), H("192.168.30.101", true),                                           // on now
            H("192.168.30.102", false, now.AddHours(-3).ToString("o")),                                      // off, but its lease is still its own
            H("192.168.30.103", false, now.AddDays(-3).ToString("o")),                                       // long gone
            H("192.168.30.104", true, ignored: true),                                                        // ignored devices aren't counted
            H("192.168.30.50", true),                                                                        // a static address outside the range
        };
        var u = Assert.Single(PoolWatch.Measure(new[] { Pool() }, hosts, now));
        Assert.Equal((10, 3, 30), (u.Size, u.Used, u.Percent));
    }

    [Fact]
    public void The_pools_you_enter_have_to_make_sense()
    {
        Assert.Null(PoolWatch.Clean(new[] { Pool(start: "192.168.30.200", end: "192.168.30.100") }).Pools);     // backwards
        Assert.Null(PoolWatch.Clean(new[] { Pool(end: "192.168.31.5") }).Pools);                                // outside the network
        Assert.Null(PoolWatch.Clean(new[] { Pool(start: "nonsense") }).Pools);
        Assert.Null(PoolWatch.Clean(new[] { Pool(net: "8.8.8.0/24", start: "8.8.8.2", end: "8.8.8.9") }).Pools);  // not private
        Assert.Null(PoolWatch.Clean(new[] { Pool(), Pool(name: "again") }).Pools);                             // one pool a network
        Assert.Null(PoolWatch.Clean(new[] { Pool() }).Error);
    }

    [Fact]
    public void A_controllers_networks_give_their_ranges_and_networks_with_no_dhcp_are_skipped()
    {
        var pools = PoolWatch.ParseNetworks(JsonNode.Parse("""
            {"data":[
              {"name":"Default","ip_subnet":"192.168.1.1/24","dhcpd_enabled":true,"dhcpd_start":"192.168.1.100","dhcpd_stop":"192.168.1.250"},
              {"name":"Servers","ip_subnet":"192.168.5.1/24","dhcpd_enabled":false,"dhcpd_start":"192.168.5.10","dhcpd_stop":"192.168.5.20"},
              {"name":"WAN","purpose":"wan"},
              {"name":"Odd","ip_subnet":"192.168.9.1/24","dhcpd_enabled":true,"dhcpd_start":"10.0.0.5","dhcpd_stop":"10.0.0.9"}
            ]}
            """));
        var p = Assert.Single(pools);
        Assert.Equal(("192.168.1.0/24", "Default", "192.168.1.100", "192.168.1.250", "unifi"), (p.Network, p.Name, p.Start, p.End, p.Source));     // the gateway's address became its network
    }

    private sealed class Controller : HttpMessageHandler
    {
        public string Networks = """{"data":[{"name":"Home","ip_subnet":"192.168.30.1/24","dhcpd_enabled":true,"dhcpd_start":"192.168.30.100","dhcpd_stop":"192.168.30.109"}]}""";
        public readonly List<string> Calls = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            Calls.Add(req.Method + " " + path);
            if (path == "/api/auth/login") return Task.FromResult(new HttpResponseMessage { Content = new StringContent("{}") });
            if (path.EndsWith("/rest/networkconf")) return Task.FromResult(new HttpResponseMessage { Content = new StringContent(Networks) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task Nothing_is_read_until_pool_watch_is_switched_on_and_then_the_controller_gives_the_range_and_yours_replaces_it()
    {
        using var app = new BamfApp();
        var store = Store(app);
        var gear = app.Services.GetRequiredService<NetworkGear>();
        var ctl = new Controller();
        gear.Unifi.Handler = ctl;
        store.SetSetting("gearUnifiUrl", "https://unifi.test"); store.SetSetting("gearUnifiUser", "bamf"); store.SetSetting("gearUnifiSecret", "x");
        for (var i = 0; i < 4; i++) Seen(store, $"AA:00:00:00:00:{i + 1:00}", $"192.168.30.{100 + i}", $"d{i}");
        var c = app.Client();
        var off = await c.GetFromJsonAsync<JsonElement>("/api/pools");
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.Empty(off.GetProperty("pools").EnumerateArray());
        Assert.Empty(ctl.Calls);

        var on = await (await c.PostAsJsonAsync("/api/settings/pools", new { enabled = true })).Content.ReadFromJsonAsync<JsonElement>();
        var p = on.GetProperty("pools").EnumerateArray().Single();
        Assert.Equal(("192.168.30.0/24", "unifi", 10, 4, 40), (p.GetProperty("network").GetString(), p.GetProperty("source").GetString(), p.GetProperty("size").GetInt32(), p.GetProperty("used").GetInt32(), p.GetProperty("percent").GetInt32()));
        Assert.Contains("GET /proxy/network/api/s/default/rest/networkconf", ctl.Calls);

        // What you enter for a network wins over what the controller says about it.
        var mine = await (await c.PostAsJsonAsync("/api/settings/pools", new { pools = new[] { new { network = Net, name = "Mine", start = "192.168.30.100", end = "192.168.30.119" } } })).Content.ReadFromJsonAsync<JsonElement>();
        var m = mine.GetProperty("pools").EnumerateArray().Single();
        Assert.Equal(("manual", 20, 20), (m.GetProperty("source").GetString(), m.GetProperty("size").GetInt32(), m.GetProperty("percent").GetInt32()));
    }

    [Fact]
    public async Task A_pool_alerts_once_when_it_crosses_the_limit_and_again_only_after_it_has_eased()
    {
        using var app = new BamfApp();
        var store = Store(app);
        var pools = app.Services.GetRequiredService<PoolWatch>();
        var c = app.Client();
        for (var i = 0; i < 4; i++) Seen(store, $"AA:00:00:00:00:{i + 1:00}", $"192.168.30.{100 + i}", $"d{i}");              // 40%
        await c.PostAsJsonAsync("/api/settings/pools", new { pools = new[] { new { network = Net, name = "Main", start = "192.168.30.100", end = "192.168.30.109" } }, percent = 60, enabled = true, alert = true });
        await pools.Run(default);
        Assert.Empty(store.GetAlerts(50));

        for (var i = 4; i < 7; i++) Seen(store, $"AA:00:00:00:00:{i + 1:00}", $"192.168.30.{100 + i}", $"d{i}");              // 70%
        await pools.Run(default);
        var alert = Assert.Single(store.GetAlerts(50));
        Assert.Equal(("pool", "The address pool for Main (192.168.30.0/24) is 70% used"), (alert.Kind, alert.Title));
        await pools.Run(default);
        Assert.Single(store.GetAlerts(50));                                                                                    // once

        Sql(store, "UPDATE hosts SET online = 0, last_seen = '2020-01-01T00:00:00.0000000Z' WHERE ip IN ('192.168.30.104', '192.168.30.105', '192.168.30.106', '192.168.30.103')");
        await pools.Run(default);                                                                                              // 30%: eased
        Sql(store, "UPDATE hosts SET online = 1 WHERE ip IN ('192.168.30.104', '192.168.30.105', '192.168.30.106', '192.168.30.103')");
        await pools.Run(default);                                                                                              // back to 70%
        Assert.Equal(2, store.GetAlerts(50).Count);
    }

    [Fact]
    public async Task Switching_the_pool_alert_on_does_not_speak_of_a_pool_that_is_already_full()
    {
        using var app = new BamfApp();
        var store = Store(app);
        var pools = app.Services.GetRequiredService<PoolWatch>();
        var c = app.Client();
        for (var i = 0; i < 9; i++) Seen(store, $"AA:00:00:00:00:{i + 1:00}", $"192.168.30.{100 + i}", $"d{i}");              // 90%
        await c.PostAsJsonAsync("/api/settings/pools", new { pools = new[] { new { network = Net, name = "Main", start = "192.168.30.100", end = "192.168.30.109" } }, enabled = true });
        await c.PostAsJsonAsync("/api/settings/pools", new { alert = true });
        await pools.Run(default);
        Assert.Empty(store.GetAlerts(50));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/pools", new { percent = 20 })).StatusCode);
    }
}
