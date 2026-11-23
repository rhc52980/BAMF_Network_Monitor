using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>The names you give your networks (Home, Guest, IoT): kept, cleaned up, returned with the device list, and carried when a range changes.</summary>
[Collection("Endpoints")]
public class NetworkLabelTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task A_name_is_kept_cleaned_up_returned_with_the_list_and_cleared_by_an_empty_one()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var res = await c.PostAsJsonAsync("/api/settings/network-label", new { network = "192.168.30.0/24", label = "   IoT \t stuff  " });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("IoT stuff", (await Json(res)).GetProperty("192.168.30.0/24").GetString());     // trimmed, spaces made single

        var list = await c.GetFromJsonAsync<JsonElement>("/api/hosts");
        Assert.Equal("IoT stuff", list.GetProperty("networkLabels").GetProperty("192.168.30.0/24").GetString());

        await c.PostAsJsonAsync("/api/settings/network-label", new { network = "192.168.20.0/24", label = "Guest" });
        var cleared = await Json(await c.PostAsJsonAsync("/api/settings/network-label", new { network = "192.168.30.0/24", label = "" }));
        Assert.False(cleared.TryGetProperty("192.168.30.0/24", out _));                                // an empty name takes it away
        Assert.Equal("Guest", cleared.GetProperty("192.168.20.0/24").GetString());                     // and only that one
    }

    [Fact]
    public async Task A_name_that_cannot_be_kept_is_refused_and_nothing_changes()
    {
        using var app = new BamfApp();
        var c = app.Client();
        await c.PostAsJsonAsync("/api/settings/network-label", new { network = "192.168.30.0/24", label = "IoT" });
        foreach (var body in new object[]
        {
            new { network = "", label = "x" },
            new { network = "192.168.30.0/24", label = new string('x', 41) },
            new { network = "192.168.30.0/24", label = "bad\u0007name" },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/network-label", body)).StatusCode);
        Assert.Equal("IoT", app.Services.GetRequiredService<HostStore>().GetNetworkLabels()["192.168.30.0/24"]);
    }

    [Fact]
    public async Task A_name_follows_its_network_to_a_new_range_and_goes_when_the_network_does()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var store = app.Services.GetRequiredService<HostStore>();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/settings/networks", new { networks = new[] { "192.168.0.0/22", "192.168.40.0/24" } })).StatusCode);
        await c.PostAsJsonAsync("/api/settings/network-label", new { network = "192.168.0.0/22", label = "Home" });
        await c.PostAsJsonAsync("/api/settings/network-label", new { network = "192.168.40.0/24", label = "Guest" });

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/settings/networks/edit", new { from = "192.168.0.0/22", to = "192.168.0.0/21" })).StatusCode);
        var labels = store.GetNetworkLabels();
        Assert.Equal("Home", labels["192.168.0.0/21"]);
        Assert.False(labels.ContainsKey("192.168.0.0/22"));

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/settings/networks/remove", new { network = "192.168.40.0/24", devices = "keep" })).StatusCode);
        Assert.False(store.GetNetworkLabels().ContainsKey("192.168.40.0/24"));
        Assert.Equal("Home", store.GetNetworkLabels()["192.168.0.0/21"]);
    }

    [Fact]
    public async Task Alerts_name_a_network_only_when_the_switch_is_on_and_the_network_has_a_name()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var store = app.Services.GetRequiredService<HostStore>();
        await c.PostAsJsonAsync("/api/settings/network-label", new { network = "192.168.30.0/24", label = "IoT" });

        Assert.False(store.NetworkNamesInAlerts);                                                       // off unless asked for
        Assert.Equal("192.168.30.0/24", store.AlertNetName("192.168.30.0/24"));                         // so the range alone, as before

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/settings/network-names-in-alerts", new { enabled = true })).StatusCode);
        Assert.Equal("IoT \u00b7 192.168.30.0/24", store.AlertNetName("192.168.30.0/24"));
        Assert.Equal("192.168.20.0/24", store.AlertNetName("192.168.20.0/24"));                         // no name: the range
        Assert.Equal("", store.AlertNetName(null));

        var settings = (await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("editable");
        Assert.True(settings.GetProperty("networkNamesInAlerts").GetBoolean());
        Assert.Contains("networkNamesInAlerts", HostStore.TransferKeys);

        await c.PostAsJsonAsync("/api/settings/network-names-in-alerts", new { enabled = false });
        Assert.Equal("192.168.30.0/24", store.AlertNetName("192.168.30.0/24"));
    }

    [Fact]
    public async Task A_segmentation_alert_says_where_by_name_with_the_switch_on_and_by_range_without()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var store = app.Services.GetRequiredService<HostStore>();
        var seg = app.Services.GetRequiredService<SegmentationCheck>();
        await c.PostAsJsonAsync("/api/settings/network-label", new { network = "192.168.30.0/24", label = "Home" });
        await c.PostAsJsonAsync("/api/settings/network-label", new { network = "192.168.20.0/24", label = "IoT" });
        await c.PostAsJsonAsync("/api/settings/segmentation", new { rules = new[] { new { label = "Cameras", tag = "", words = new[] { "cam" }, type = "", where = "on", network = "192.168.20.0/24" } }, enabled = true });
        await c.PostAsJsonAsync("/api/settings/segmentation", new { alert = true });
        await seg.Run(default);

        store.UpsertSeen("AA:00:00:00:00:01", "192.168.30.5", "porch-cam", "Acme", "192.168.30.0/24");
        await seg.Run(default);
        var plain = Assert.Single(store.GetAlerts(50));
        Assert.Equal("porch-cam is on 192.168.30.0/24, outside your \"Cameras\" rule", plain.Title);       // switch off: as it always was
        Assert.Contains("belong on 192.168.20.0/24", plain.Detail);

        await c.PostAsJsonAsync("/api/settings/network-names-in-alerts", new { enabled = true });
        store.UpsertSeen("AA:00:00:00:00:02", "192.168.30.6", "yard-cam", "Acme", "192.168.30.0/24");
        await seg.Run(default);
        var named = store.GetAlerts(50).First(a => a.Title.StartsWith("yard-cam"));
        Assert.Equal("yard-cam is on Home \u00b7 192.168.30.0/24, outside your \"Cameras\" rule", named.Title);
        Assert.Contains("belong on IoT \u00b7 192.168.20.0/24", named.Detail);
    }
}
