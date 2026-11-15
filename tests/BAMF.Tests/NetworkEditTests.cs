using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>Changing a network's range in place, and what happens to its devices when a network is taken away.</summary>
[Collection("Endpoints")]
public class NetworkEditTests
{
    private const string Wide = "192.168.0.0/22";
    private const string Wider = "192.168.0.0/21";
    private const string Other = "192.168.40.0/24";

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static (HostStore Store, HttpClient C) Set(BamfApp app, params string[] networks)
    {
        var c = app.Client();
        Assert.Equal(HttpStatusCode.OK, c.PostAsJsonAsync("/api/settings/networks", new { networks }).Result.StatusCode);
        return (app.Services.GetRequiredService<HostStore>(), c);
    }

    private static void Seen(HostStore store, string mac, string ip, string net) => store.UpsertSeen(mac, ip, "host-" + mac[^2..], "Acme", net);

    private static HostRecord Host(HostStore store, string ip) => store.GetAll().First(h => h.Ip == ip);

    [Fact]
    public async Task Widening_a_network_moves_its_devices_and_keeps_its_pause_and_interval()
    {
        using var app = new BamfApp();
        var (store, c) = Set(app, Wide, Other);
        Seen(store, "AA:00:00:00:00:01", "192.168.1.5", Wide);
        Seen(store, "AA:00:00:00:00:02", "192.168.3.9", Wide);
        Seen(store, "AA:00:00:00:00:03", "192.168.40.7", Other);
        store.SetSetting("subnetScanIntervalSeconds", $$"""{"{{Wide}}":120,"{{Other}}":90}""");
        store.SetSetting("disabledSubnets", $$"""["{{Wide}}"]""");

        var res = await c.PostAsJsonAsync("/api/settings/networks/edit", new { from = Wide, to = "192.168.1.77/21" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var d = await Json(res);
        Assert.Equal(2, d.GetProperty("moved").GetInt32());
        Assert.Equal(0, d.GetProperty("left").GetInt32());
        Assert.Equal(new[] { Wider, Other }, d.GetProperty("networks").GetProperty("list").EnumerateArray().Select(x => x.GetString()).ToArray());   // same place in the list

        Assert.Equal(Wider, Host(store, "192.168.1.5").Subnet);
        Assert.Equal(Wider, Host(store, "192.168.3.9").Subnet);
        Assert.Equal(Other, Host(store, "192.168.40.7").Subnet);                       // another network is left alone
        Assert.DoesNotContain(store.GetAll(), h => h.Subnet == Wide);                  // no zombie

        Assert.Equal(120, JsonSerializer.Deserialize<Dictionary<string, int>>(store.GetSetting("subnetScanIntervalSeconds")!)![Wider]);
        Assert.Equal(90, JsonSerializer.Deserialize<Dictionary<string, int>>(store.GetSetting("subnetScanIntervalSeconds")!)![Other]);
        Assert.Equal(new[] { Wider }, JsonSerializer.Deserialize<string[]>(store.GetSetting("disabledSubnets")!));
        Assert.DoesNotContain(Wide, store.GetSetting("subnetScanIntervalSeconds")!);
    }

    [Fact]
    public async Task A_change_that_cannot_be_made_is_refused_and_nothing_is_saved()
    {
        using var app = new BamfApp();
        var (store, c) = Set(app, Wide, Other);
        Seen(store, "AA:00:00:00:00:01", "192.168.1.5", Wide);
        foreach (var body in new object[]
        {
            new { from = "10.9.9.0/24", to = Wider },        // not one of the networks
            new { from = Wide, to = "192.168.1.9/22" },      // the same network, written another way
            new { from = Wide, to = Other },                 // already on the list
            new { from = Wide, to = "192.168.0.0/20" },      // wider than a sweep covers
            new { from = Wide, to = "8.8.8.0/24" },          // not private
            new { from = Wide, to = "nonsense" },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/networks/edit", body)).StatusCode);
        Assert.Equal(new[] { Wide, Other }, JsonSerializer.Deserialize<string[]>(store.GetSetting("subnets")!));
        Assert.Equal(Wide, Host(store, "192.168.1.5").Subnet);
    }

    [Fact]
    public async Task Moving_to_a_range_that_holds_only_some_leaves_the_rest_and_forgets_them_on_request()
    {
        using var app = new BamfApp();
        var (store, c) = Set(app, "192.168.0.0/23", Other);
        Seen(store, "AA:00:00:00:00:01", "192.168.0.5", "192.168.0.0/23");
        Seen(store, "AA:00:00:00:00:02", "192.168.1.9", "192.168.0.0/23");

        var usage = await Json(await c.GetAsync("/api/settings/networks/usage?network=192.168.0.0/23&to=192.168.0.0/24"));
        Assert.Equal(1, usage.GetProperty("fitsNew").GetInt32());

        var res = await Json(await c.PostAsJsonAsync("/api/settings/networks/edit", new { from = "192.168.0.0/23", to = "192.168.0.0/24", forgetLeft = true }));
        Assert.Equal(1, res.GetProperty("moved").GetInt32());
        Assert.Equal(1, res.GetProperty("forgotten").GetInt32());
        Assert.Equal(0, res.GetProperty("left").GetInt32());
        Assert.Equal("192.168.0.0/24", Host(store, "192.168.0.5").Subnet);
        Assert.False(Host(store, "192.168.0.5").Forgotten);
        Assert.True(Host(store, "192.168.1.9").Forgotten);                              // hidden, not deleted
    }

    [Fact]
    public async Task Without_forgetting_the_ones_that_do_not_fit_are_counted_as_left()
    {
        using var app = new BamfApp();
        var (store, c) = Set(app, "192.168.0.0/23", Other);
        Seen(store, "AA:00:00:00:00:01", "192.168.0.5", "192.168.0.0/23");
        Seen(store, "AA:00:00:00:00:02", "192.168.1.9", "192.168.0.0/23");
        var res = await Json(await c.PostAsJsonAsync("/api/settings/networks/edit", new { from = "192.168.0.0/23", to = "192.168.0.0/24" }));
        Assert.Equal(1, res.GetProperty("left").GetInt32());
        Assert.Equal("192.168.0.0/23", Host(store, "192.168.1.9").Subnet);
        Assert.False(Host(store, "192.168.1.9").Forgotten);
    }

    [Fact]
    public async Task Usage_counts_devices_and_offers_the_other_networks_that_hold_them()
    {
        using var app = new BamfApp();
        var (store, c) = Set(app, Wide, "192.168.8.0/21", Other);
        Seen(store, "AA:00:00:00:00:01", "192.168.1.5", Wide);
        Seen(store, "AA:00:00:00:00:02", "192.168.2.9", Wide);
        store.SetForgotten(Host(store, "192.168.2.9").Id, true);

        var u = await Json(await c.GetAsync($"/api/settings/networks/usage?network={Uri.EscapeDataString(Wide)}"));
        Assert.Equal(1, u.GetProperty("devices").GetInt32());
        Assert.Equal(1, u.GetProperty("forgotten").GetInt32());
        Assert.Equal(JsonValueKind.Null, u.GetProperty("fitsNew").ValueKind);
        Assert.Empty(u.GetProperty("moveTo").EnumerateArray());                        // neither of the others holds a device in 192.168.1.x
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/settings/networks/usage")).StatusCode);
    }

    [Fact]
    public async Task Removing_a_network_can_keep_its_devices_as_it_always_did()
    {
        using var app = new BamfApp();
        var (store, c) = Set(app, Wide, Other);
        Seen(store, "AA:00:00:00:00:01", "192.168.1.5", Wide);
        var res = await Json(await c.PostAsJsonAsync("/api/settings/networks/remove", new { network = Wide }));
        Assert.Equal(new[] { Other }, res.GetProperty("networks").GetProperty("list").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(1, res.GetProperty("left").GetInt32());
        Assert.Equal(Wide, Host(store, "192.168.1.5").Subnet);
        Assert.False(Host(store, "192.168.1.5").Forgotten);
    }

    [Fact]
    public async Task Removing_a_network_can_forget_its_devices_which_the_forgotten_tab_brings_back()
    {
        using var app = new BamfApp();
        var (store, c) = Set(app, Wide, Other);
        Seen(store, "AA:00:00:00:00:01", "192.168.1.5", Wide);
        Seen(store, "AA:00:00:00:00:03", "192.168.40.7", Other);
        var res = await Json(await c.PostAsJsonAsync("/api/settings/networks/remove", new { network = Wide, devices = "forget" }));
        Assert.Equal(1, res.GetProperty("forgotten").GetInt32());
        Assert.True(Host(store, "192.168.1.5").Forgotten);
        Assert.False(Host(store, "192.168.40.7").Forgotten);                           // only that network's
        Assert.Contains(store.GetAll(), h => h.Ip == "192.168.1.5");                   // still there to restore
    }

    [Fact]
    public async Task Removing_a_network_can_move_its_devices_into_one_that_holds_them()
    {
        using var app = new BamfApp();
        var (store, c) = Set(app, Wide, Wider, Other);                                  // a /22 inside a /21
        Seen(store, "AA:00:00:00:00:01", "192.168.1.5", Wide);
        Seen(store, "AA:00:00:00:00:04", "192.168.6.5", Wide);                          // a stray: inside the /21, outside the /22
        var u = await Json(await c.GetAsync($"/api/settings/networks/usage?network={Uri.EscapeDataString(Wide)}"));
        Assert.Equal(2, u.GetProperty("moveTo").EnumerateArray().First(x => x.GetProperty("network").GetString() == Wider).GetProperty("count").GetInt32());

        var res = await Json(await c.PostAsJsonAsync("/api/settings/networks/remove", new { network = Wide, devices = "move", moveTo = Wider }));
        Assert.Equal(2, res.GetProperty("moved").GetInt32());
        Assert.Equal(Wider, Host(store, "192.168.1.5").Subnet);
        Assert.DoesNotContain(store.GetAll(), h => h.Subnet == Wide);
    }

    [Fact]
    public async Task A_removal_that_cannot_be_done_is_refused()
    {
        using var app = new BamfApp();
        var (store, c) = Set(app, Wide, Other);
        Seen(store, "AA:00:00:00:00:01", "192.168.1.5", Wide);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/networks/remove", new { network = "10.1.1.0/24" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/networks/remove", new { network = Wide, devices = "delete" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/networks/remove", new { network = Wide, devices = "move", moveTo = "10.1.1.0/24" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/settings/networks/remove", new { network = Wide })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/networks/remove", new { network = Other })).StatusCode);   // the last one stays
        Assert.Equal(new[] { Other }, JsonSerializer.Deserialize<string[]>(store.GetSetting("subnets")!));
    }
}
