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
}
