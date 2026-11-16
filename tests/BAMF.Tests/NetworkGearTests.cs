using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>
/// Network gear: the Map drawn from a controller or a switch's bridge table, Wi-Fi quality, and the gear's health.
/// Each is off until switched on, never replaces what was recorded by hand, and is taken back when switched off.
/// </summary>
[Collection("Endpoints")]
public class NetworkGearTests
{
    // ---- the controller, played by a handler

    private const string Gw = "AA:00:00:00:00:01", Core = "AA:00:00:00:00:02", Ap = "AA:00:00:00:00:03", Lite = "AA:00:00:00:00:04";
    private const string Pc = "BB:00:00:00:00:01", Laptop = "BB:00:00:00:00:02", Phone = "BB:00:00:00:00:03", Tv = "BB:00:00:00:00:04";

    private sealed class Controller : HttpMessageHandler
    {
        public string Devices = DevicesJson(), Clients = ClientsJson(), Alarms = """{"data":[]}""";
        public int Status = 200;
        public readonly List<string> Calls = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            Calls.Add(req.Method + " " + path);
            if (path == "/api/auth/login")
            {
                var login = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                login.Headers.Add("X-CSRF-Token", "token");
                return Task.FromResult(login);
            }
            if (Status != 200) return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent("{}") });
            if (path.EndsWith("/stat/device")) return Task.FromResult(new HttpResponseMessage { Content = new StringContent(Devices) });
            if (path.EndsWith("/stat/sta")) return Task.FromResult(new HttpResponseMessage { Content = new StringContent(Clients) });
            if (path.EndsWith("/stat/alarm")) return Task.FromResult(new HttpResponseMessage { Content = new StringContent(Alarms) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private const string LiteJson = """
        ,{"mac":"aa:00:00:00:00:04","name":"Garage switch","model":"USWLITE8P","type":"usw","version":"7.0.0","state":1,"ip":"192.168.30.4","uptime":700000,
         "uplink":{"uplink_mac":"aa:00:00:00:00:02","uplink_remote_port":16},
         "port_table":[{"port_idx":1,"up":true,"speed":1000},{"port_idx":3,"up":true,"speed":1000}]}
        """;

    private static string DevicesJson(long apUptime = 900_000, bool upgradable = true, bool includeLite = true, bool wan1Active = true, bool wan2Up = true) => """
        {"data":[
          {"mac":"aa:00:00:00:00:01","name":"Gateway","model":"UCGMAX","type":"udm","version":"4.1.13","state":1,"ip":"192.168.30.1","uptime":2000000,
           "port_table":[{"port_idx":1,"up":true,"speed":1000},{"port_idx":2,"up":true,"speed":1000},{"port_idx":3,"up":false,"speed":0}],
           "system-stats":{"cpu":"4.5","mem":"41.0"},
           "wan1":{"name":"wan","up":true,"is_uplink":@WAN1ACTIVE@,"ip":"203.0.113.5"},
           "wan2":{"name":"wan2","up":@WAN2UP@,"is_uplink":@WAN2ACTIVE@,"ip":"198.51.100.9"}},
          {"mac":"aa:00:00:00:00:02","name":"Core switch","model":"USPM16P","type":"usw","version":"7.2.1","state":1,"ip":"192.168.30.2","uptime":1500000,
           "upgradable":@UPGRADABLE@,"upgrade_to_firmware":"7.2.9","total_max_power":180,"general_temperature":52,
           "uplink":{"uplink_mac":"aa:00:00:00:00:01","uplink_remote_port":1},
           "port_table":[{"port_idx":1,"name":"Uplink","up":true,"speed":2500,"poe_power":"0.00"},{"port_idx":2,"up":true,"speed":1000,"poe_power":"0.00"},
                         {"port_idx":3,"up":true,"speed":100,"poe_power":"0.00"},{"port_idx":5,"up":true,"speed":2500,"poe_power":"14.50"},{"port_idx":16,"up":true,"speed":1000,"poe_power":"3.20"}]},
          {"mac":"aa:00:00:00:00:03","name":"Kitchen AP","model":"U7PROXG","type":"uap","version":"8.0.1","state":1,"ip":"192.168.30.3","uptime":@APUPTIME@,
           "uplink":{"uplink_mac":"aa:00:00:00:00:02","uplink_remote_port":5},
           "vap_table":[{"essid":"Home","radio":"ng"},{"essid":"Home","radio":"na"},{"essid":"Guest","radio":"ng"}]}
          @LITE@
        ]}
        """.Replace("@UPGRADABLE@", upgradable ? "true" : "false").Replace("@APUPTIME@", apUptime.ToString()).Replace("@LITE@", includeLite ? LiteJson : "").Replace("@WAN1ACTIVE@", wan1Active ? "true" : "false").Replace("@WAN2ACTIVE@", wan1Active ? "false" : "true").Replace("@WAN2UP@", wan2Up ? "true" : "false");

    private static string ClientsJson(string laptopAp = "aa:00:00:00:00:03", bool phoneWired = false) => """
        {"data":[
          {"mac":"bb:00:00:00:00:01","ip":"192.168.30.10","is_wired":true,"sw_mac":"aa:00:00:00:00:02","sw_port":2},
          {"mac":"bb:00:00:00:00:02","ip":"192.168.30.11","is_wired":false,"ap_mac":"@LAPTOPAP@","essid":"Home","radio":"na","channel":44,"signal":-52,"noise":-95,"tx_rate":866000,"rx_rate":720000,"satisfaction":98,"roam_count":2},
          {"mac":"bb:00:00:00:00:03","ip":"192.168.30.12","is_wired":@PHONEWIRED@,"ap_mac":"aa:00:00:00:00:03","essid":"Guest","radio":"ng","channel":6,"signal":-79,"tx_rate":58000,"rx_rate":52000,"satisfaction":41,
           "sw_mac":"aa:00:00:00:00:02","sw_port":3},
          {"mac":"bb:00:00:00:00:04","ip":"192.168.30.13","is_wired":true,"sw_mac":"aa:00:00:00:00:04","sw_port":3},
          {"mac":"aa:00:00:00:00:03","ip":"192.168.30.3","is_wired":true,"sw_mac":"aa:00:00:00:00:02","sw_port":5}
        ]}
        """.Replace("@LAPTOPAP@", laptopAp).Replace("@PHONEWIRED@", phoneWired ? "true" : "false");

    private static (NetworkGear Gear, HostStore Store, Controller C, HttpClient Http) Set(BamfApp app, bool seedHosts = true)
    {
        var gear = app.Services.GetRequiredService<NetworkGear>();
        var store = app.Services.GetRequiredService<HostStore>();
        var c = new Controller();
        gear.Unifi.Handler = c;
        store.SetSetting("gearUnifiUrl", "https://unifi.test");
        store.SetSetting("gearUnifiUser", "bamf");
        store.SetSetting("gearUnifiSecret", "view-only-pass");
        if (seedHosts)
        {
            var net = "192.168.30.0/24";
            store.UpsertSeen(Gw, "192.168.30.1", "gateway", "Ubiquiti", net);
            store.UpsertSeen(Core, "192.168.30.2", "core", "Ubiquiti", net);
            store.UpsertSeen(Ap, "192.168.30.3", "kitchen-ap", "Ubiquiti", net);
            store.UpsertSeen(Lite, "192.168.30.4", "garage", "Ubiquiti", net);
            store.UpsertSeen(Pc, "192.168.30.10", "pc", "Dell", net);
            store.UpsertSeen(Laptop, "192.168.30.11", "laptop", "Apple", net);
            store.UpsertSeen(Phone, "192.168.30.12", "phone", "Samsung", net);
            store.UpsertSeen(Tv, "192.168.30.13", "tv", "LG", net);
        }
        return (gear, store, c, app.Client());
    }

    private static HostRecord Host(HostStore store, string mac) => store.GetAll().First(h => h.Mac == mac);
    private static SwitchRecord Sw(HostStore store, string name) => store.GetSwitches().First(s => s.Name == name);
    private static string Where(HostStore store, string mac)
    {
        var p = store.GetPlacements();
        if (!p.TryGetValue(Host(store, mac).Id, out var pl)) return "";
        return store.GetSwitches().First(s => s.Id == pl.SwitchId).Name + ":" + pl.Port;
    }

    // ---- what it says is read right

    [Fact]
    public void A_controllers_two_lists_are_read_into_gear_and_clients()
    {
        var snap = UniFiGear.Parse(JsonNode.Parse(DevicesJson()), JsonNode.Parse(ClientsJson()), DateTime.UtcNow);
        Assert.Equal(new[] { "router", "switch", "ap", "switch" }, snap.Devices.Select(d => d.Kind));
        var core = snap.Devices.First(d => d.Mac == Core);
        Assert.Equal((Gw, 1), (core.UplinkMac, core.UplinkPort));
        Assert.Equal("7.2.9", core.UpgradeTo);
        Assert.Equal(17.7, core.PoeWatts);                         // the ports' draw, added up
        Assert.Equal(180, core.PoeBudget);
        Assert.Equal(52, core.TempC);
        Assert.Equal(16, core.PortCount);                          // as many ports as the highest number it lists
        Assert.Equal(4.5, snap.Devices.First(d => d.Mac == Gw).Cpu);   // the controller sends its CPU as text
        var ap = snap.Devices.First(d => d.Mac == Ap);
        Assert.Equal(new[] { "Home", "Guest" }, ap.Ssids.Select(s => s.Name));
        Assert.Equal("2.4 GHz, 5 GHz", ap.Ssids[0].Bands);

        Assert.DoesNotContain(snap.Clients, c => c.Mac == Ap);       // the gear itself isn't a client
        var laptop = snap.Clients.First(c => c.Mac == Laptop);
        Assert.False(laptop.Wired);
        Assert.Equal((Ap, "Home", "5 GHz", 44, -52, 866, 720, 98, 2), (laptop.ViaMac, laptop.Ssid, laptop.Band, laptop.Channel, laptop.Signal, laptop.TxMbps, laptop.RxMbps, laptop.Satisfaction, laptop.Roams));
        var pc = snap.Clients.First(c => c.Mac == Pc);
        Assert.Equal((true, Core, 2), (pc.Wired, pc.ViaMac, pc.Port));
    }

    [Theory]
    [InlineData(-50, "excellent", false)]
    [InlineData(-60, "good", false)]
    [InlineData(-72, "fair", false)]
    [InlineData(-80, "weak", true)]
    public void A_signal_is_put_in_words(int dbm, string word, bool weak)
    {
        Assert.Equal(word, WifiQuality.Label(dbm));
        Assert.Equal(weak, WifiQuality.Weak(dbm));
    }

    // ---- off until switched on

    [Fact]
    public async Task Nothing_is_read_or_drawn_until_a_feature_is_switched_on()
    {
        using var app = new BamfApp();
        var (gear, store, c, _) = Set(app);
        Assert.True(await gear.Refresh(true, default));
        Assert.Empty(c.Calls);
        Assert.Empty(store.GetSwitches());
        Assert.Empty(store.GetGear());
        var d = await app.Client().GetFromJsonAsync<JsonElement>("/api/gear");
        Assert.False(d.GetProperty("features").GetProperty("map").GetBoolean());
        Assert.False(d.GetProperty("features").GetProperty("wifi").GetBoolean());
        Assert.False(d.GetProperty("features").GetProperty("health").GetBoolean());
    }

    // ---- the Map

    [Fact]
    public async Task The_map_is_drawn_from_the_controller_and_every_device_put_where_it_is()
    {
        using var app = new BamfApp();
        var (gear, store, _, http) = Set(app);
        var res = await http.PostAsJsonAsync("/api/settings/gear", new { map = true });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);                      // switching it on reads at once

        var sw = store.GetSwitches();
        Assert.Equal(new[] { "router", "switch", "ap", "switch" }, new[] { "Gateway", "Core switch", "Kitchen AP", "Garage switch" }.Select(n => Sw(store, n).Kind));
        Assert.Equal(2, sw.Count(s => s.Kind == "ssid"));                     // Home and Guest, on the AP
        Assert.All(sw.Where(s => s.Kind == "ssid"), s => Assert.Equal(Sw(store, "Kitchen AP").Id, s.RunsOn));

        // The cabling, as the controller reports it.
        var core = Sw(store, "Core switch");
        Assert.Equal(("router", Host(store, Core).Id), (core.Uplink, core.HostId));
        Assert.Equal(("switch", core.Id, 5), (Sw(store, "Kitchen AP").Uplink, Sw(store, "Kitchen AP").UplinkSwitch, Sw(store, "Kitchen AP").UplinkPort));
        Assert.Equal(("switch", core.Id, 16), (Sw(store, "Garage switch").Uplink, Sw(store, "Garage switch").UplinkSwitch, Sw(store, "Garage switch").UplinkPort));
        Assert.Equal(16, core.Ports);                                         // room for the port the garage switch is on
        Assert.Equal("192.168.30.0/24", core.Subnet);

        // The devices: wired on a port, Wi-Fi on the network it joined. The gear isn't placed as a device.
        Assert.Equal("Core switch:2", Where(store, Pc));
        Assert.Equal("Garage switch:3", Where(store, Tv));
        Assert.Equal("Home:0", Where(store, Laptop));
        Assert.Equal("Guest:0", Where(store, Phone));
        Assert.Equal("", Where(store, Ap));

        // Read again: nothing new is made, nothing moves.
        var ids = store.GetSwitches().Select(s => s.Id).OrderBy(i => i).ToList();
        Assert.True(await gear.Refresh(true, default));
        Assert.Equal(ids, store.GetSwitches().Select(s => s.Id).OrderBy(i => i).ToList());
        Assert.Equal(4, store.CountAutoPlacements());
    }

    [Fact]
    public async Task A_placement_and_a_switch_you_recorded_are_never_replaced()
    {
        using var app = new BamfApp();
        var (gear, store, _, http) = Set(app);
        // The core switch is already recorded by hand, linked to its device, and the PC is plugged into port 9 by hand.
        var mine = store.SaveSwitch(null, new SwitchInput("My core", 24, "", Host(store, Core).Id, "", 0, 0, "switch", 0)).Saved!;
        Assert.Null(store.SetPlacement(Host(store, Pc).Id, mine.Id, 9));

        await http.PostAsJsonAsync("/api/settings/gear", new { map = true });

        Assert.DoesNotContain(store.GetSwitches(), s => s.Name == "Core switch");   // no second record for the same device
        Assert.Equal("My core:9", Where(store, Pc));                                // the hand-made placement stands
        var my = Sw(store, "My core");
        Assert.Equal((24, "", ""), (my.Ports, my.Uplink, store.GetAutoSwitchSources().GetValueOrDefault(my.Id) ?? ""));   // untouched
        // The garage switch hangs off it, since that's where the controller says it is.
        Assert.Equal(("switch", mine.Id, 16), (Sw(store, "Garage switch").Uplink, Sw(store, "Garage switch").UplinkSwitch, Sw(store, "Garage switch").UplinkPort));
        Assert.Equal("Garage switch:3", Where(store, Tv));
    }

    [Fact]
    public async Task Switching_the_map_off_takes_back_what_it_drew_and_keeps_what_you_built_on_it()
    {
        using var app = new BamfApp();
        var (_, store, _, http) = Set(app);
        await http.PostAsJsonAsync("/api/settings/gear", new { map = true });
        // The user puts a device on an automatic switch by hand: that switch is now something they rely on.
        Assert.Null(store.SetPlacement(Host(store, Phone).Id, Sw(store, "Garage switch").Id, 3));

        var res = await http.PostAsJsonAsync("/api/settings/gear", new { map = false });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // Only what their placement rests on stays: that switch, and the one it's plugged into.
        Assert.Equal(new[] { "Core switch", "Garage switch" }, store.GetSwitches().Select(s => s.Name).OrderBy(n => n).ToArray());
        Assert.Empty(store.GetAutoSwitches());                                                          // and they're theirs now
        Assert.Equal("Garage switch:3", Where(store, Phone));
        Assert.Equal("", Where(store, Pc));                                                             // everything drawn for them is gone
    }

    [Fact]
    public async Task Editing_a_drawn_switch_by_hand_makes_it_yours()
    {
        using var app = new BamfApp();
        var (_, store, _, http) = Set(app);
        await http.PostAsJsonAsync("/api/settings/gear", new { map = true });
        var garage = Sw(store, "Garage switch");
        Assert.Equal("unifi", store.GetAutoSwitchSources()[garage.Id]);
        var edit = await http.PostAsJsonAsync($"/api/switches/{garage.Id}", new { name = "Garage", ports = 8, subnet = garage.Subnet, hostId = garage.HostId, uplink = "switch", uplinkSwitch = garage.UplinkSwitch, uplinkPort = garage.UplinkPort, kind = "switch" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.False(store.GetAutoSwitchSources().ContainsKey(garage.Id));

        await http.PostAsJsonAsync("/api/settings/gear", new { map = false });
        Assert.Contains(store.GetSwitches(), s => s.Name == "Garage");                                  // kept: it's theirs
    }

    [Fact]
    public async Task Gear_the_controller_no_longer_lists_comes_off_the_map()
    {
        using var app = new BamfApp();
        var (gear, store, c, http) = Set(app);
        await http.PostAsJsonAsync("/api/settings/gear", new { map = true });
        Assert.Contains(store.GetSwitches(), s => s.Name == "Garage switch");
        c.Devices = DevicesJson(includeLite: false);
        await gear.Refresh(true, default);
        Assert.DoesNotContain(store.GetSwitches(), s => s.Name == "Garage switch");
        Assert.Equal("", Where(store, Tv));
    }

    [Fact]
    public async Task A_controller_that_cannot_be_read_leaves_what_was_drawn_and_says_why()
    {
        using var app = new BamfApp();
        var (gear, store, c, http) = Set(app);
        await http.PostAsJsonAsync("/api/settings/gear", new { map = true });
        var before = store.GetSwitches().Count;
        c.Status = 500;
        Assert.True(await gear.Refresh(true, default));
        Assert.Equal(before, store.GetSwitches().Count);
        var d = await http.GetFromJsonAsync<JsonElement>("/api/gear");
        var src = d.GetProperty("sources").EnumerateArray().First(s => s.GetProperty("id").GetString() == "unifi");
        Assert.Equal("error", src.GetProperty("state").GetString());
        Assert.Contains("500", src.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_drawn_switch_you_delete_is_not_drawn_again_until_you_start_over()
    {
        using var app = new BamfApp();
        var (gear, store, _, http) = Set(app);
        await http.PostAsJsonAsync("/api/settings/gear", new { map = true });
        Assert.Equal(HttpStatusCode.OK, (await http.DeleteAsync($"/api/switches/{Sw(store, "Garage switch").Id}")).StatusCode);
        await gear.Refresh(true, default);
        Assert.DoesNotContain(store.GetSwitches(), s => s.Name == "Garage switch");   // the controller still lists it, and you said no
        Assert.Equal("", Where(store, Tv));
        Assert.Equal("Core switch:2", Where(store, Pc));                               // the rest is as it was

        await http.PostAsJsonAsync("/api/settings/gear", new { map = false });
        await http.PostAsJsonAsync("/api/settings/gear", new { map = true });
        Assert.Contains(store.GetSwitches(), s => s.Name == "Garage switch");          // switching it off and on again starts afresh
    }

    [Fact]
    public async Task A_wireless_network_is_on_the_network_most_of_its_devices_are()
    {
        using var app = new BamfApp();
        var (_, store, _, http) = Set(app);
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath}"))   // the phone is on the guest network
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE hosts SET subnet = '192.168.209.0/24', ip = '192.168.209.12' WHERE mac = $m";
            cmd.Parameters.AddWithValue("$m", Phone);
            cmd.ExecuteNonQuery();
        }
        await http.PostAsJsonAsync("/api/settings/gear", new { map = true });
        Assert.Equal("192.168.209.0/24", Sw(store, "Guest").Subnet);
        Assert.Equal("192.168.30.0/24", Sw(store, "Home").Subnet);
    }

    [Fact]
    public async Task Switching_a_feature_off_waits_for_a_read_in_progress_and_leaves_nothing_behind()
    {
        using var app = new BamfApp();
        var (gear, store, c, http) = Set(app);
        await http.PostAsJsonAsync("/api/settings/gear", new { map = true, wifi = true, health = true });
        var reads = Enumerable.Range(0, 3).Select(_ => gear.Refresh(true, default)).ToList();     // a burst of reads, one gets through
        await http.PostAsJsonAsync("/api/settings/gear", new { map = false, wifi = false, health = false });
        await Task.WhenAll(reads);
        Assert.Empty(store.GetSwitches());
        Assert.Empty(store.GetWifi());
        Assert.Empty(store.GetGear());
        Assert.Equal(0, store.CountAutoPlacements());
        _ = c;
    }

    // ---- Wi-Fi

    [Fact]
    public async Task Wifi_readings_are_kept_per_device_and_a_move_between_access_points_is_noticed()
    {
        using var app = new BamfApp();
        var (gear, store, c, http) = Set(app);
        await http.PostAsJsonAsync("/api/settings/gear", new { wifi = true });
        Assert.Empty(store.GetSwitches());                                                  // Wi-Fi alone draws nothing on the Map

        var w = store.GetWifi();
        Assert.Equal(2, w.Count);
        var l = w[Host(store, Laptop).Id];
        Assert.Equal(("Kitchen AP", "Home", "5 GHz", 44, -52, 866), (l.ApName, l.Ssid, l.Band, l.Channel, l.Signal, l.TxMbps));
        Assert.Equal(("", 2), (l.MovedFrom, l.Roams));

        // The laptop walks to another access point.
        c.Clients = ClientsJson(laptopAp: Lite.ToLowerInvariant());
        await gear.Refresh(true, default);
        var moved = store.GetWifi()[Host(store, Laptop).Id];
        Assert.Equal(("Kitchen AP", Lite), (moved.MovedFrom, moved.ApMac));
        Assert.NotEqual("", moved.MovedAt);

        // A device that goes onto a cable has no Wi-Fi reading any more.
        c.Clients = ClientsJson(phoneWired: true);
        await gear.Refresh(true, default);
        Assert.DoesNotContain(store.GetWifi(), p => p.Key == Host(store, Phone).Id);

        var d = await http.GetFromJsonAsync<JsonElement>("/api/gear");
        Assert.Equal(1, d.GetProperty("wifi").GetArrayLength());
    }

    [Fact]
    public async Task Switching_wifi_off_forgets_the_readings()
    {
        using var app = new BamfApp();
        var (_, store, _, http) = Set(app);
        await http.PostAsJsonAsync("/api/settings/gear", new { wifi = true });
        Assert.NotEmpty(store.GetWifi());
        await http.PostAsJsonAsync("/api/settings/gear", new { wifi = false });
        Assert.Empty(store.GetWifi());
    }

    [Fact]
    public async Task The_weak_signal_shows_up_in_the_list_the_weakest_first()
    {
        using var app = new BamfApp();
        var (_, _, _, http) = Set(app);
        await http.PostAsJsonAsync("/api/settings/gear", new { wifi = true });
        var d = await http.GetFromJsonAsync<JsonElement>("/api/gear");
        var list = d.GetProperty("wifi").EnumerateArray().ToList();
        Assert.Equal("phone", list[0].GetProperty("name").GetString());
        Assert.True(list[0].GetProperty("weak").GetBoolean());
        Assert.Equal("weak", list[0].GetProperty("quality").GetString());
        Assert.Equal("excellent", list[1].GetProperty("quality").GetString());
    }

    // ---- health

    [Fact]
    public async Task Gear_health_is_recorded_and_what_needs_a_look_is_said()
    {
        using var app = new BamfApp();
        var (_, store, _, http) = Set(app);
        await http.PostAsJsonAsync("/api/settings/gear", new { health = true });
        Assert.Empty(store.GetSwitches());                                                  // health alone draws nothing either

        var d = await http.GetFromJsonAsync<JsonElement>("/api/gear");
        var gear = d.GetProperty("gear").EnumerateArray().ToList();
        Assert.Equal(4, gear.Count);
        var core = gear.First(g => g.GetProperty("name").GetString() == "Core switch");
        Assert.Equal("7.2.1", core.GetProperty("version").GetString());
        Assert.Equal("7.2.9", core.GetProperty("upgradeTo").GetString());
        Assert.Equal(17.7, core.GetProperty("poe").GetDouble());
        Assert.Equal(5, core.GetProperty("portsUp").GetInt32());
        var issues = core.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("text").GetString()).ToList();
        Assert.Contains("Firmware 7.2.9 is available.", issues);
        Assert.Contains("Port 3 is linked at 100 Mbps.", issues);                           // the others run at a gigabit or more
        Assert.Equal(0, core.GetProperty("attention").GetInt32());                          // informational only: nothing urgent
    }

    [Fact]
    public async Task A_restart_is_counted_when_the_uptime_goes_down()
    {
        using var app = new BamfApp();
        var (gear, store, c, http) = Set(app);
        await http.PostAsJsonAsync("/api/settings/gear", new { health = true });
        Assert.Equal(0, store.GetGear().First(g => g.Mac == Ap).Reboots);

        c.Devices = DevicesJson(apUptime: 120);                                              // up 900,000 s, then up 2 minutes
        await gear.Refresh(true, default);
        var ap = store.GetGear().First(g => g.Mac == Ap);
        Assert.Equal(1, ap.Reboots);
        Assert.NotEqual("", ap.LastReboot);

        var d = await http.GetFromJsonAsync<JsonElement>("/api/gear");
        var row = d.GetProperty("gear").EnumerateArray().First(g => g.GetProperty("name").GetString() == "Kitchen AP");
        Assert.Equal(1, row.GetProperty("attention").GetInt32());
        Assert.Contains(row.GetProperty("issues").EnumerateArray(), i => i.GetProperty("text").GetString()!.StartsWith("It restarted"));

        // A little jitter between two reads is not a restart.
        c.Devices = DevicesJson(apUptime: 150);
        await gear.Refresh(true, default);
        Assert.Equal(1, store.GetGear().First(g => g.Mac == Ap).Reboots);
    }

    [Fact]
    public async Task Switching_health_off_forgets_it()
    {
        using var app = new BamfApp();
        var (_, store, _, http) = Set(app);
        await http.PostAsJsonAsync("/api/settings/gear", new { health = true });
        Assert.NotEmpty(store.GetGear());
        await http.PostAsJsonAsync("/api/settings/gear", new { health = false });
        Assert.Empty(store.GetGear());
    }

    // ---- the login

    [Fact]
    public async Task The_password_is_never_sent_back_and_forgetting_the_controller_takes_back_what_it_gave()
    {
        using var app = new BamfApp();
        var (_, store, _, http) = Set(app);
        await http.PostAsJsonAsync("/api/settings/gear", new { map = true, wifi = true, health = true });
        var body = await (await http.GetAsync("/api/gear")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("view-only-pass", body);
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("unifi").GetProperty("hasSecret").GetBoolean());

        var res = await http.PostAsJsonAsync("/api/settings/gear", new { unifiUrl = "" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("", store.GetSetting("gearUnifiSecret"));
        Assert.Empty(store.GetSwitches());
        Assert.Empty(store.GetWifi());
        Assert.Empty(store.GetGear());
        Assert.Equal(0, store.CountAutoPlacements());
    }

    [Theory]
    [InlineData("https://192.168.1.1", "https://192.168.1.1")]
    [InlineData("  https://unifi.local:8443/network/default/dashboard  ", "https://unifi.local:8443")]
    public void The_controllers_address_is_tidied(string typed, string expected) => Assert.Equal(expected, LanWatch.Api.GearEndpoints.CleanUrl(typed).Url);

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("ftp://192.168.1.1")]
    [InlineData("https://admin:pass@192.168.1.1")]
    public async Task An_address_that_cannot_be_used_is_refused(string typed)
    {
        using var app = new BamfApp();
        var res = await app.Client().PostAsJsonAsync("/api/settings/gear", new { unifiUrl = typed });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task The_router_import_login_is_used_when_none_is_saved_here()
    {
        using var app = new BamfApp(settings: new()
        {
            ["Bamf:RouterImport:Kind"] = "unifi", ["Bamf:RouterImport:Url"] = "https://192.168.1.1/",
            ["Bamf:RouterImport:Username"] = "bamf", ["Bamf:RouterImport:Password"] = "file-pass",
        });
        var gear = app.Services.GetRequiredService<NetworkGear>();
        var (s, fromImport) = gear.UniFiSettings()!.Value;
        Assert.True(fromImport);
        Assert.Equal(("https://192.168.1.1", "bamf", "file-pass"), (s.Url, s.User, s.Secret));
        var d = await app.Client().GetFromJsonAsync<JsonElement>("/api/gear");
        Assert.True(d.GetProperty("unifi").GetProperty("fromRouterImport").GetBoolean());
        Assert.DoesNotContain("file-pass", await app.Client().GetStringAsync("/api/gear"));
    }

    // ---- any managed switch, over SNMP

    [Fact]
    public void A_device_is_on_the_port_that_has_learned_the_fewest_others_and_never_on_a_port_that_leads_on_to_other_gear()
    {
        // Core (1) has the router on port 1, an access point on port 5 with two Wi-Fi devices behind it, and a PC on port 2.
        // Garage (2) is plugged into core's port 16, so core learned the TV on port 16; garage learned it on its own port 3.
        var core = new FdbPlacement.Table(1, new() { ["GW"] = 1, ["AP"] = 5, ["LAPTOP"] = 5, ["PHONE"] = 5, ["PC"] = 2, ["GARAGE"] = 16, ["TV"] = 16 });
        var garage = new FdbPlacement.Table(2, new() { ["CORE"] = 1, ["GW"] = 1, ["TV"] = 3, ["PC"] = 1 });
        var done = FdbPlacement.Decide(new[] { core, garage }, new HashSet<string> { "GW", "AP", "GARAGE", "CORE" });
        Assert.Equal((1L, 2), done["PC"]);
        Assert.Equal((2L, 3), done["TV"]);                                   // the real port, not core's way-on port 16
        Assert.False(done.ContainsKey("LAPTOP"));                            // behind an access point: left for the Wi-Fi source
        Assert.False(done.ContainsKey("PHONE"));
        Assert.False(done.ContainsKey("GW"));                                // gear isn't placed as a device
    }

    [Fact]
    public void Two_equally_good_ports_are_left_alone_rather_than_guessed()
    {
        var a = new FdbPlacement.Table(1, new() { ["X"] = 4 });
        var b = new FdbPlacement.Table(2, new() { ["X"] = 7 });
        Assert.Empty(FdbPlacement.Decide(new[] { a, b }, new HashSet<string>()));
    }

    [Fact]
    public async Task A_switch_read_over_snmp_places_devices_on_its_ports_and_reports_its_health()
    {
        using var agent = new FakeSnmpAgent();
        // 8 ports gi1..gi8, ifIndex 1..8. Learned: the PC on 2, the TV on 3, the gateway on 1 (so port 1 leads on and is left alone).
        for (var i = 1; i <= 8; i++)
        {
            agent.Set($"1.3.6.1.2.1.2.2.1.3.{i}", 2, 6L);                        // ethernetCsmacd
            agent.Set($"1.3.6.1.2.1.31.1.1.1.1.{i}", 4, $"gi{i}");
            agent.Set($"1.3.6.1.2.1.2.2.1.8.{i}", 2, i is 2 or 3 ? 1L : 2L);    // oper up on 2 and 3
            agent.Set($"1.3.6.1.2.1.31.1.1.1.15.{i}", 0x42, i == 2 ? 1000UL : i == 3 ? 100UL : 0UL);
            agent.Set($"1.3.6.1.2.1.17.1.4.1.2.{i}", 2, (long)i);               // bridge port i is ifIndex i
        }
        agent.Fdb(1, Gw, 1); agent.Fdb(1, Pc, 2); agent.Fdb(1, Tv, 3);
        agent.Set("1.3.6.1.2.1.1.1.0", 4, "Acme 8-port managed switch, firmware 2.1");
        agent.Set("1.3.6.1.2.1.1.3.0", 0x43, 8_640_000UL);                      // 86,400 s
        agent.Set("1.3.6.1.2.1.1.5.0", 4, "acme-sw");
        agent.Set("1.3.6.1.2.1.105.1.3.1.1.2.1", 0x42, 120UL);                  // PoE budget 120 W
        agent.Set("1.3.6.1.2.1.105.1.3.1.1.4.1", 0x42, 30UL);                   // 30 W drawn

        using var app = new BamfApp();
        var (gear, store, _, http) = Set(app);
        store.SetSetting("gearUnifiUrl", "");                                    // no controller: only the switch
        var sw = store.SaveSwitch(null, new SwitchInput("Acme", 8, "", Host(store, Core).Id, "", 0, 0, "switch", 0)).Saved!;
        var gateway = store.SaveSwitch(null, new SwitchInput("Router", 4, "", Host(store, Gw).Id, "", 0, 0, "router", 0)).Saved!;
        store.SaveSnmpConfig(sw.Id, true, $"127.0.0.1:{agent.Port}", "public");
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/api/settings/gear", new { map = true, health = true })).StatusCode);

        Assert.Equal("Acme:2", Where(store, Pc));
        Assert.Equal("Acme:3", Where(store, Tv));
        Assert.Equal("", Where(store, Gw));
        Assert.Equal(2, store.CountAutoPlacements());

        var row = store.GetGear().First(g => g.Mac == Core);
        Assert.Equal(("snmp", 86_400L, 30.0, 120.0), (row.Source, row.UptimeSeconds, row.PoeWatts, row.PoeBudget));
        Assert.Equal(new[] { 2, 3 }, row.Ports.Where(p => p.Up).Select(p => p.Port).ToArray());
        var d = await http.GetFromJsonAsync<JsonElement>("/api/gear");
        Assert.Contains(d.GetProperty("sources").EnumerateArray(), s => s.GetProperty("id").GetString() == $"snmp:{sw.Id}" && s.GetProperty("state").GetString() == "ok");

        // A hand-made placement wins over the switch's table.
        Assert.Null(store.SetPlacement(Host(store, Pc).Id, sw.Id, 6));
        await gear.Refresh(true, default);
        Assert.Equal("Acme:6", Where(store, Pc));
        _ = gateway;
    }

    [Fact]
    public async Task A_switch_that_does_not_answer_is_reported_and_the_rest_still_works()
    {
        using var app = new BamfApp();
        var (_, store, _, http) = Set(app);
        var sw = store.SaveSwitch(null, new SwitchInput("Silent", 8, "", 0, "", 0, 0, "switch", 0)).Saved!;
        using var closed = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)closed.Client.LocalEndPoint!).Port;
        closed.Close();                                                         // nothing is listening there now
        store.SaveSnmpConfig(sw.Id, true, $"127.0.0.1:{port}", "public");
        await http.PostAsJsonAsync("/api/settings/gear", new { map = true });
        var d = await http.GetFromJsonAsync<JsonElement>("/api/gear");
        var src = d.GetProperty("sources").EnumerateArray().First(s => s.GetProperty("id").GetString() == $"snmp:{sw.Id}");
        Assert.Equal("error", src.GetProperty("state").GetString());
        Assert.Contains(store.GetSwitches(), s => s.Name == "Core switch");     // the controller's gear was still drawn
    }

    // ---- devices on networks read from the gateway

    private const string Guest = "192.168.10.0/24";

    private static string VlanClients(bool includeCamera = true) => """
        {"data":[
          {"mac":"cc:00:00:00:00:01","ip":"192.168.10.21","is_wired":false,"ap_mac":"aa:00:00:00:00:03","essid":"Guest","radio":"ng","channel":6,"signal":-60,"name":"Dad's iPad","oui":"Apple, Inc."},
          {"mac":"cc:00:00:00:00:02","ip":"192.168.10.22","is_wired":true,"sw_mac":"aa:00:00:00:00:02","sw_port":7,"hostname":"printer"},
          @CAMERA@
          {"mac":"bb:00:00:00:00:01","ip":"192.168.30.10","is_wired":true,"sw_mac":"aa:00:00:00:00:02","sw_port":2}
        ]}
        """.Replace("@CAMERA@", includeCamera ? """{"mac":"cc:00:00:00:00:03","ip":"192.168.10.23","is_wired":true,"sw_mac":"aa:00:00:00:00:02","sw_port":8,"name":"Porch camera"},""" : "");

    private static async Task<JsonElement> Gear(HttpClient http) => await http.GetFromJsonAsync<JsonElement>("/api/gear");

    [Fact]
    public async Task A_network_read_from_the_gateway_gets_its_devices_with_the_names_the_controller_knows()
    {
        using var app = new BamfApp();
        var (gear, store, c, http) = Set(app);
        c.Clients = VlanClients();
        var res = await http.PostAsJsonAsync("/api/settings/gear", new { vlans = true, vlanNetworks = new[] { Guest } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var guests = store.GetAll().Where(h => h.Subnet == Guest).OrderBy(h => h.Ip).ToList();
        Assert.Equal(new[] { "192.168.10.21", "192.168.10.22", "192.168.10.23" }, guests.Select(h => h.Ip).ToArray());
        Assert.Equal(new[] { "Dad's iPad", "printer", "Porch camera" }, guests.Select(h => h.Hostname).ToArray());
        Assert.Equal("Apple, Inc.", guests[0].Vendor);                                // the controller's own word for it
        Assert.All(guests, h => Assert.True(h.Online));
        Assert.DoesNotContain(guests, h => h.Ip.StartsWith("192.168.30."));          // the scanned network's devices are not taken over
        Assert.Equal("192.168.30.0/24", Host(store, Pc).Subnet);

        // They show on the dashboard like any network's, marked as read from the gateway.
        var hosts = await http.GetFromJsonAsync<JsonElement>("/api/hosts");
        Assert.Equal("gateway", hosts.GetProperty("scanModes").GetProperty(Guest).GetString());
        var d = await Gear(http);
        var net = d.GetProperty("vlans").GetProperty("networks").EnumerateArray().Single();
        Assert.Equal((Guest, 3, 3), (net.GetProperty("network").GetString(), net.GetProperty("devices").GetInt32(), net.GetProperty("online").GetInt32()));
        _ = gear;
    }

    [Fact]
    public async Task A_device_the_gateway_stops_listing_goes_offline_after_as_many_reads_as_a_scanned_one_may_miss()
    {
        using var app = new BamfApp();
        var (gear, store, c, http) = Set(app);
        c.Clients = VlanClients();
        await http.PostAsJsonAsync("/api/settings/gear", new { vlans = true, vlanNetworks = new[] { Guest } });
        var camera = store.GetAll().First(h => h.Mac == "CC:00:00:00:00:03");

        c.Clients = VlanClients(includeCamera: false);
        await gear.Refresh(true, default);
        Assert.True(store.GetAll().First(h => h.Id == camera.Id).Online);            // one miss is allowed (two by default)
        await gear.Refresh(true, default);
        Assert.False(store.GetAll().First(h => h.Id == camera.Id).Online);
        Assert.True(store.GetAll().First(h => h.Mac == "CC:00:00:00:00:01").Online);

        c.Clients = VlanClients();                                                    // and back again
        await gear.Refresh(true, default);
        Assert.True(store.GetAll().First(h => h.Id == camera.Id).Online);
    }

    [Fact]
    public void The_scanner_leaves_a_network_read_from_the_gateway_alone()
    {
        using var app = new BamfApp();
        var (_, store, _, _) = Set(app);
        store.UpsertSeen("CC:00:00:00:00:09", "192.168.10.9", "cam", "Acme", Guest);
        store.UpsertSeen("DD:00:00:00:00:09", "192.168.30.99", "pc2", "Acme", "192.168.30.0/24");
        var scanned = new[] { "192.168.30.0/24" };
        // What the scanner does when every scanned network was covered and the gateway's is on the list: only what it covered is judged.
        store.MarkOffline(new HashSet<string>(), scanned, 1, scanned.Concat(new[] { Guest }).ToList());
        Assert.False(store.GetAll().First(h => h.Mac == "DD:00:00:00:00:09").Online);
        Assert.True(store.GetAll().First(h => h.Mac == "CC:00:00:00:00:09").Online);
        // Whereas a network that's on no list at all is judged on every pass, which is what the gateway's would have met without this.
        store.MarkOffline(new HashSet<string>(), scanned, 1, scanned);
        Assert.False(store.GetAll().First(h => h.Mac == "CC:00:00:00:00:09").Online);
    }

    [Fact]
    public async Task Switching_it_off_hides_the_devices_and_switching_it_on_again_brings_them_back_with_their_names()
    {
        using var app = new BamfApp();
        var (_, store, c, http) = Set(app);
        c.Clients = VlanClients();
        await http.PostAsJsonAsync("/api/settings/gear", new { vlans = true, vlanNetworks = new[] { Guest } });
        var ipad = store.GetAll().First(h => h.Mac == "CC:00:00:00:00:01");
        store.SetName(ipad.Id, "The iPad");                                     // yours

        await http.PostAsJsonAsync("/api/settings/gear", new { vlans = false });
        Assert.All(store.GetAll().Where(h => h.Subnet == Guest), h => Assert.True(h.Forgotten));
        Assert.Equal("The iPad", store.GetAll().First(h => h.Id == ipad.Id).CustomName);       // hidden, not deleted
        Assert.Equal("192.168.30.0/24", Host(store, Pc).Subnet);                                // nothing else touched
        Assert.False(Host(store, Pc).Forgotten);

        await http.PostAsJsonAsync("/api/settings/gear", new { vlans = true });                 // the list of networks was kept
        var back = store.GetAll().First(h => h.Id == ipad.Id);
        Assert.False(back.Forgotten);
        Assert.Equal("The iPad", back.CustomName);
    }

    [Fact]
    public async Task Taking_a_network_off_the_list_hides_its_devices()
    {
        using var app = new BamfApp();
        var (_, store, c, http) = Set(app);
        c.Clients = VlanClients();
        await http.PostAsJsonAsync("/api/settings/gear", new { vlans = true, vlanNetworks = new[] { Guest } });
        await http.PostAsJsonAsync("/api/settings/gear", new { vlanNetworks = Array.Empty<string>() });
        Assert.All(store.GetAll().Where(h => h.Subnet == Guest), h => Assert.True(h.Forgotten));
        Assert.Empty(store.GearVlanNetworkList());
    }

    [Theory]
    [InlineData("8.8.8.0/24")]                    // not private
    [InlineData("192.168.30.0/24")]               // BAMF scans it itself
    [InlineData("192.168.30.128/25")]             // inside one it scans
    [InlineData("192.168.0.0/16")]                // wider than BAMF reads
    [InlineData("nonsense")]
    public async Task A_network_that_cannot_be_read_from_the_gateway_is_refused(string network)
    {
        using var app = new BamfApp();
        var (_, store, _, http) = Set(app);
        var res = await http.PostAsJsonAsync("/api/settings/gear", new { vlans = true, vlanNetworks = new[] { network } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(store.GearVlanNetworkList());
    }

    [Fact]
    public async Task Networks_the_gateway_lists_but_you_have_not_added_are_suggested()
    {
        using var app = new BamfApp();
        var (_, store, c, http) = Set(app);
        c.Clients = VlanClients();
        await http.PostAsJsonAsync("/api/settings/gear", new { vlans = true });
        var d = await Gear(http);
        var s = d.GetProperty("vlans").GetProperty("suggestions").EnumerateArray().Single();
        Assert.Equal((Guest, 3), (s.GetProperty("network").GetString(), s.GetProperty("devices").GetInt32()));
        Assert.DoesNotContain(store.GetAll(), h => h.Subnet == Guest);             // suggesting adds nothing
    }

    [Fact]
    public async Task A_routers_arp_table_over_snmp_lists_the_devices_on_every_network_it_is_on()
    {
        using var agent = new FakeSnmpAgent();
        agent.Arp(3, "192.168.10.41", "DD:00:00:00:00:41");
        agent.Arp(3, "192.168.10.42", "DD:00:00:00:00:42");
        agent.Arp(1, "192.168.30.10", "BB:00:00:00:00:01");                         // on a network that's scanned: not taken
        using var app = new BamfApp();
        var (_, store, _, http) = Set(app);
        store.SetSetting("gearUnifiUrl", "");
        var router = store.SaveSwitch(null, new SwitchInput("Router", 4, "", Host(store, Gw).Id, "", 0, 0, "router", 0)).Saved!;
        store.SaveSnmpConfig(router.Id, true, $"127.0.0.1:{agent.Port}", "public");
        var res = await http.PostAsJsonAsync("/api/settings/gear", new { vlans = true, vlanNetworks = new[] { Guest } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(new[] { "DD:00:00:00:00:41", "DD:00:00:00:00:42" }, store.GetAll().Where(h => h.Subnet == Guest).OrderBy(h => h.Ip).Select(h => h.Mac).ToArray());
        var src = (await Gear(http)).GetProperty("sources").EnumerateArray().First(x => x.GetProperty("id").GetString() == $"snmp:{router.Id}");
        Assert.Equal("ok", src.GetProperty("state").GetString());
        Assert.Contains("3 ARP entries", src.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task An_opnsense_firewalls_arp_table_lists_the_devices_too()
    {
        using var app = new BamfApp(settings: new()
        {
            ["Bamf:RouterImport:Kind"] = "opnsense", ["Bamf:RouterImport:Url"] = "https://fw.test", ["Bamf:RouterImport:ApiKey"] = "k", ["Bamf:RouterImport:ApiSecret"] = "s",
        });
        var gear = app.Services.GetRequiredService<NetworkGear>();
        var store = app.Services.GetRequiredService<HostStore>();
        var fw = new FakeFirewall();
        gear.Opn.Handler = fw;
        await app.Client().PostAsJsonAsync("/api/settings/gear", new { vlans = true, vlanNetworks = new[] { Guest } });
        var found = store.GetAll().Where(h => h.Subnet == Guest).OrderBy(h => h.Ip).ToList();
        Assert.Equal(new[] { "192.168.10.51", "192.168.10.52" }, found.Select(h => h.Ip).ToArray());
        Assert.Equal(new[] { "tv", "" }, found.Select(h => h.Hostname).ToArray());          // a domain is dropped from a name
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("k:s")), fw.Auth);
        Assert.Contains("GET /api/diagnostics/interface/getArp", fw.Calls);
    }

    private sealed class FakeFirewall : HttpMessageHandler
    {
        public readonly List<string> Calls = new();
        public string? Auth;
        public string Gateways = """{"items":[{"name":"WAN_DHCP","address":"203.0.113.5","status":"none"},{"name":"WAN2","address":"198.51.100.9","status":"none"}]}""";
        public string Alerts = """{"rows":[],"total":0}""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Auth = req.Headers.Authorization?.ToString();
            Calls.Add(req.Method + " " + req.RequestUri!.AbsolutePath);
            var body = req.RequestUri.AbsolutePath switch
            {
                "/api/diagnostics/interface/getArp" => """[{"mac":"dd:00:00:00:00:51","ip":"192.168.10.51","hostname":"tv.lan","manufacturer":"LG"},{"mac":"dd:00:00:00:00:52","ip":"192.168.10.52","hostname":"","manufacturer":""},{"mac":"bb:00:00:00:00:01","ip":"192.168.30.10","hostname":"","manufacturer":""}]""",
                "/api/routes/gateway/status" => Gateways,
                "/api/ids/service/queryAlerts" => Alerts,
                _ => null,
            };
            return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage { Content = new StringContent(body) });
        }
    }

    // ---- the gateway's own events

    [Fact]
    public void A_failover_a_lost_connection_and_its_return_are_noticed_and_the_first_sight_is_not()
    {
        GearDevice Gateway(bool wan1Up, bool wan1Active, bool wan2Up, bool wan2Active) => new("unifi", Gw, "Gateway", "router", "", "", "", 0, true, "", 0, "", 0, null, null, null, null, null, new(), new(),
            new() { new GearWan("wan", wan1Up, "", wan1Active), new GearWan("wan2", wan2Up, "", wan2Active) });
        var now = DateTime.UtcNow;
        var (m1, e1) = GatewayEvents.Step(new(), new[] { Gateway(true, true, true, false) }, now);
        Assert.Empty(e1);                                                                       // first sight: only remembered
        var (m2, e2) = GatewayEvents.Step(m1, new[] { Gateway(true, true, true, false) }, now);
        Assert.Empty(e2);                                                                       // nothing changed
        var (m3, e3) = GatewayEvents.Step(m2, new[] { Gateway(false, false, true, true) }, now);
        Assert.Equal(2, e3.Count);                                                              // wan went down, and traffic moved
        Assert.Contains(e3, e => e.Title == "Internet connection wan is down on Gateway" && e.Detail.Contains("another connection is still up"));
        Assert.Contains(e3, e => e.Title == "Gateway moved your internet from wan to wan2");
        var (_, e4) = GatewayEvents.Step(m3, new[] { Gateway(true, true, true, false) }, now);
        Assert.Contains(e4, e => e.Title == "Internet connection wan is back on Gateway");
        Assert.Contains(e4, e => e.Title == "Gateway moved your internet from wan2 to wan");
    }

    [Fact]
    public void A_batch_of_threats_is_one_alert_that_says_how_many_and_what()
    {
        GearEvent T(string sig, bool blocked) => new("unifi", DateTime.UtcNow.ToString("o"), "threat", sig, "1.2.3.4 to 192.168.30.10", blocked);
        var one = GatewayEvents.Summarize(new[] { T("ET SCAN Nmap", true) });
        Assert.Equal("Your gateway blocked a threat: ET SCAN Nmap", one.Title);
        var many = GatewayEvents.Summarize(new[] { T("ET SCAN Nmap", true), T("ET SCAN Nmap", true), T("ET POLICY curl", false) });
        Assert.Equal("Your gateway's intrusion detection saw 3 threats (2 blocked)", many.Title);
        Assert.Contains("2 x ET SCAN Nmap", many.Detail);
        Assert.Contains("1 were only detected, not stopped", many.Detail);
    }

    [Fact]
    public void The_controllers_intrusion_events_and_the_firewalls_alerts_are_read()
    {
        var uni = UniFiGear.ParseThreats(JsonNode.Parse("""
            {"data":[
              {"key":"EVT_IPS_IpsAlert","time":1800000002000,"inner_alert_signature":"ET SCAN Nmap","inner_alert_category":"Attempted Information Leak","src_ip":"203.0.113.9","src_port":"4444","dest_ip":"192.168.30.10","inner_alert_action":"blocked"},
              {"key":"EVT_AD_Login","time":1800000003000,"msg":"admin logged in"},
              {"key":"EVT_IPS_IpsAlert","time":1800000001000,"msg":"Something","dest_ip":"192.168.30.11","inner_alert_action":"alert"}
            ]}
            """));
        Assert.Equal(2, uni.Count);                                                             // a login isn't a threat
        Assert.Equal(("ET SCAN Nmap", true), (uni[0].Title, uni[0].Blocked));                   // newest first
        Assert.Equal("Attempted Information Leak. 203.0.113.9:4444 to 192.168.30.10 (blocked)", uni[0].Detail);
        Assert.False(uni[1].Blocked);

        var opn = OpnSenseGear.ParseThreats(JsonNode.Parse("""
            {"rows":[{"timestamp":"2026-11-15T10:00:00.000000+0000","alert":"ET POLICY curl","alert_action":"allowed","src_ip":"10.1.1.5","dest_ip":"1.1.1.1"},
                     {"timestamp":"2026-11-15T11:00:00.000000+0000","alert":"ET SCAN ssh","alert_action":"blocked","src_ip":"203.0.113.9","src_port":"22","dest_ip":"192.168.1.5"}]}
            """));
        Assert.Equal(new[] { "ET SCAN ssh", "ET POLICY curl" }, opn.Select(e => e.Title).ToArray());
        Assert.True(opn[0].Blocked);
    }

    private static string Alarm(long ms, string sig, string action = "blocked") =>
        $$"""{"key":"EVT_IPS_IpsAlert","time":{{ms}},"inner_alert_signature":"{{sig}}","src_ip":"203.0.113.9","dest_ip":"192.168.30.10","inner_alert_action":"{{action}}"}""";

    [Fact]
    public async Task Gateway_events_do_nothing_until_a_kind_is_ticked()
    {
        using var app = new BamfApp();
        var (gear, store, c, http) = Set(app);
        await http.PostAsJsonAsync("/api/settings/gear", new { events = true });                // the feature on, no kind chosen
        Assert.DoesNotContain(c.Calls, x => x.EndsWith("/stat/alarm"));
        Assert.Empty(store.GetAlerts(50));
        c.Devices = DevicesJson(wan1Active: false);                                              // a failover the controller reports
        await gear.Refresh(true, default);
        Assert.Empty(store.GetAlerts(50));
        Assert.Empty(store.GetGearEventLog());
    }

    [Fact]
    public async Task A_failover_is_raised_as_an_internet_alert_once_and_the_first_read_is_only_remembered()
    {
        using var app = new BamfApp();
        var (gear, store, c, http) = Set(app);
        await http.PostAsJsonAsync("/api/settings/gear", new { events = true, eventWan = true });
        Assert.Empty(store.GetAlerts(50));                                                       // the first read says nothing
        await gear.Refresh(true, default);
        Assert.Empty(store.GetAlerts(50));

        c.Devices = DevicesJson(wan1Active: false);                                              // traffic moved to wan2
        await gear.Refresh(true, default);
        var alert = Assert.Single(store.GetAlerts(50));
        Assert.Equal(("internet", "Gateway moved your internet from wan to wan2"), (alert.Kind, alert.Title));
        await gear.Refresh(true, default);
        Assert.Single(store.GetAlerts(50));                                                      // not again while it stays so
        Assert.Equal("Gateway moved your internet from wan to wan2", Assert.Single((await Gear(http)).GetProperty("events").GetProperty("recent").EnumerateArray()).GetProperty("title").GetString());

        // Switched off, it forgets; on again it begins from how things are then (the failover is the starting point, so it isn't announced again)...
        await http.PostAsJsonAsync("/api/settings/gear", new { eventWan = false });
        await http.PostAsJsonAsync("/api/settings/gear", new { eventWan = true });
        Assert.Single(store.GetAlerts(50));
        // ...and what changes after that is.
        c.Devices = DevicesJson(wan1Active: true);
        await gear.Refresh(true, default);
        Assert.Equal(2, store.GetAlerts(50).Count);
    }

    [Fact]
    public async Task Threats_are_baselined_then_one_alert_covers_a_burst_and_no_more_than_one_every_ten_minutes()
    {
        using var app = new BamfApp();
        var (gear, store, c, http) = Set(app);
        c.Alarms = $$"""{"data":[{{Alarm(1_800_000_000_000, "OLD ONE")}}]}""";
        await http.PostAsJsonAsync("/api/settings/gear", new { events = true, eventThreats = true });
        Assert.Empty(store.GetAlerts(50));                                                       // what was logged before is history

        c.Alarms = $$"""{"data":[{{Alarm(1_800_000_090_000, "ET SCAN Nmap")}},{{Alarm(1_800_000_060_000, "ET SCAN Nmap")}},{{Alarm(1_800_000_030_000, "ET POLICY curl", "alert")}},{{Alarm(1_800_000_000_000, "OLD ONE")}}]}""";
        await gear.Refresh(true, default);
        var alert = Assert.Single(store.GetAlerts(50));
        Assert.Equal(("security", "Your gateway's intrusion detection saw 3 threats (2 blocked)"), (alert.Kind, alert.Title));
        Assert.DoesNotContain("OLD ONE", alert.Detail);

        c.Alarms = $$"""{"data":[{{Alarm(1_800_000_190_000, "ET SCAN ssh")}}]}""";
        await gear.Refresh(true, default);
        Assert.Single(store.GetAlerts(50));                                                      // too soon: held for the next alert
        var src = (await Gear(http)).GetProperty("sources").EnumerateArray().First(x => x.GetProperty("id").GetString() == "unifi:threats");
        Assert.Equal("ok", src.GetProperty("state").GetString());
    }

    [Fact]
    public async Task An_opnsense_firewalls_gateways_and_alerts_are_read_for_events()
    {
        using var app = new BamfApp(settings: new()
        {
            ["Bamf:RouterImport:Kind"] = "opnsense", ["Bamf:RouterImport:Url"] = "https://fw.test", ["Bamf:RouterImport:ApiKey"] = "k", ["Bamf:RouterImport:ApiSecret"] = "s",
        });
        var gear = app.Services.GetRequiredService<NetworkGear>();
        var store = app.Services.GetRequiredService<HostStore>();
        var fw = new FakeFirewall();
        gear.Opn.Handler = fw;
        await app.Client().PostAsJsonAsync("/api/settings/gear", new { events = true, eventWan = true, eventThreats = true });
        Assert.Empty(store.GetAlerts(50));
        fw.Gateways = """{"items":[{"name":"WAN_DHCP","address":"203.0.113.5","status":"down"},{"name":"WAN2","address":"198.51.100.9","status":"none"}]}""";
        fw.Alerts = """{"rows":[{"timestamp":"2099-01-01T00:00:00.000000+0000","alert":"ET SCAN ssh","alert_action":"blocked","src_ip":"203.0.113.9","dest_ip":"192.168.1.5"}]}""";
        await gear.Refresh(true, default);
        var titles = store.GetAlerts(50).Select(a => a.Title).ToList();
        Assert.Contains("Internet connection WAN_DHCP is down on OPNsense", titles);
        Assert.Contains("Your gateway blocked a threat: ET SCAN ssh", titles);
        Assert.Contains("POST /api/ids/service/queryAlerts", fw.Calls);
    }

    [Fact]
    public async Task Switching_events_off_forgets_where_they_had_got_to()
    {
        using var app = new BamfApp();
        var (_, store, _, http) = Set(app);
        await http.PostAsJsonAsync("/api/settings/gear", new { events = true, eventWan = true, eventThreats = true });
        Assert.NotNull(store.GetSetting("gearWanState"));
        await http.PostAsJsonAsync("/api/settings/gear", new { events = false });
        Assert.Equal("", store.GetSetting("gearWanState"));
        Assert.Equal("", store.GetSetting("gearThreatMark:unifi"));
        Assert.Empty(store.GetGearEventLog());
    }

    // ---- a tiny SNMP switch to talk to

    private sealed class FakeSnmpAgent : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly SortedDictionary<ulong[], (byte Type, object Value)> _mib = new(new OidOrder());
        private readonly CancellationTokenSource _stop = new();
        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        public FakeSnmpAgent() => _ = Task.Run(Serve);

        public void Set(string oid, byte type, object value) => _mib[Parse(oid)] = (type, value);

        /// <summary>One ARP entry the way IP-MIB lists it: ifIndex and the address in the key, the MAC's six bytes as the value.</summary>
        public void Arp(int ifIndex, string ip, string mac)
        {
            Set($"1.3.6.1.2.1.4.22.1.2.{ifIndex}.{ip}", 0x04, mac.Split(':').Select(x => Convert.ToByte(x, 16)).ToArray());
            Set($"1.3.6.1.2.1.4.22.1.4.{ifIndex}.{ip}", 2, 3L);   // dynamic
        }

        /// <summary>One learned MAC on a bridge port, the way Q-BRIDGE-MIB lists it: VLAN, then the six bytes.</summary>
        public void Fdb(int vlan, string mac, int bridgePort)
        {
            var bytes = mac.Split(':').Select(x => Convert.ToInt32(x, 16));
            Set($"1.3.6.1.2.1.17.7.1.2.2.1.2.{vlan}.{string.Join(".", bytes)}", 2, (long)bridgePort);
            Set($"1.3.6.1.2.1.17.7.1.2.2.1.3.{vlan}.{string.Join(".", bytes)}", 2, 3L);   // learned
        }

        private static ulong[] Parse(string oid) => oid.Split('.').Select(ulong.Parse).ToArray();

        private sealed class OidOrder : IComparer<ulong[]>
        {
            public int Compare(ulong[]? a, ulong[]? b)
            {
                for (var i = 0; i < Math.Min(a!.Length, b!.Length); i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
                return a!.Length.CompareTo(b!.Length);
            }
        }

        private async Task Serve()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var got = await _udp.ReceiveAsync(_stop.Token);
                    var reply = Answer(got.Buffer);
                    if (reply is not null) await _udp.SendAsync(reply, got.RemoteEndPoint, _stop.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { }
            }
        }

        // The request: SEQUENCE { version, community, GetBulk { id, nonRepeaters, maxRepetitions, SEQUENCE { SEQUENCE { oid, NULL } } } }.
        private byte[]? Answer(byte[] b)
        {
            var at = 0;
            var msg = Tlv(b, ref at);
            var m = msg.Start;
            var version = Tlv(b, ref m); var community = Tlv(b, ref m);
            var pdu = Tlv(b, ref m);
            var p = pdu.Start;
            var id = Tlv(b, ref p); Tlv(b, ref p); var max = Tlv(b, ref p);
            var list = Tlv(b, ref p);
            var vb = list.Start;
            var one = Tlv(b, ref vb);
            var o = one.Start;
            var oidTlv = Tlv(b, ref o);
            var from = DecodeOid(b, oidTlv.Start, oidTlv.Length);
            var repetitions = (int)Number(b, max.Start, max.Length);

            var order = new OidOrder();
            var next = _mib.Where(kv => order.Compare(kv.Key, from) > 0).Take(repetitions).ToList();
            var binds = new List<byte>();
            foreach (var (oid, (type, value)) in next)
                binds.AddRange(Wrap(0x30, Concat(Wrap(0x06, EncodeOid(oid)), Value(type, value))));
            var response = Wrap(0xA2, Concat(Wrap(0x02, Bytes(Number(b, id.Start, id.Length))), Wrap(0x02, new byte[] { 0 }), Wrap(0x02, new byte[] { 0 }), Wrap(0x30, binds.ToArray())));
            _ = version;
            return Wrap(0x30, Concat(Wrap(0x02, new byte[] { 1 }), Wrap(0x04, b[community.Start..(community.Start + community.Length)]), response));
        }

        private static byte[] Value(byte type, object v) => type switch
        {
            0x02 => Wrap(0x02, Bytes((long)v)),
            0x04 => Wrap(0x04, v is byte[] raw ? raw : Encoding.ASCII.GetBytes((string)v)),
            _ => Wrap(type, Bytes((long)(ulong)v)),                              // gauges and time ticks
        };

        private static byte[] Bytes(long v)
        {
            var list = new List<byte>();
            do { list.Insert(0, (byte)(v & 0xFF)); v >>= 8; } while (v != 0 && v != -1);
            if (list[0] >= 0x80) list.Insert(0, 0);
            return list.ToArray();
        }

        private static byte[] EncodeOid(ulong[] n)
        {
            var o = new List<byte> { (byte)(n[0] * 40 + n[1]) };
            foreach (var x in n.Skip(2))
            {
                var chunk = new Stack<byte>();
                var v = x;
                chunk.Push((byte)(v & 0x7F));
                while ((v >>= 7) > 0) chunk.Push((byte)(0x80 | (v & 0x7F)));
                o.AddRange(chunk);
            }
            return o.ToArray();
        }

        private static ulong[] DecodeOid(byte[] b, int start, int len)
        {
            var parts = new List<ulong> { (ulong)(b[start] / 40), (ulong)(b[start] % 40) };
            ulong cur = 0;
            for (var i = 1; i < len; i++)
            {
                cur = (cur << 7) | (uint)(b[start + i] & 0x7F);
                if ((b[start + i] & 0x80) == 0) { parts.Add(cur); cur = 0; }
            }
            return parts.ToArray();
        }

        private static long Number(byte[] b, int start, int len)
        {
            long v = len > 0 && (b[start] & 0x80) != 0 ? -1 : 0;
            for (var i = 0; i < len; i++) v = (v << 8) | b[start + i];
            return v;
        }

        private static (int Start, int Length) Tlv(byte[] b, ref int at)
        {
            at++;
            int len = b[at++];
            if ((len & 0x80) != 0) { var n = len & 0x7F; len = 0; for (var i = 0; i < n; i++) len = (len << 8) | b[at++]; }
            var start = at;
            at += len;
            return (start, len);
        }

        private static byte[] Wrap(byte type, byte[] content)
        {
            var len = content.Length;
            var head = len < 0x80 ? new[] { type, (byte)len } : len < 0x100 ? new[] { type, (byte)0x81, (byte)len } : new[] { type, (byte)0x82, (byte)(len >> 8), (byte)len };
            return Concat(head, content);
        }

        private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        public void Dispose() { _stop.Cancel(); _udp.Dispose(); }
    }
}
