using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>Saved views of the device list: saved by name, replaced rather than doubled, limited, checked, and removed.</summary>
[Collection("Endpoints")]
public class SavedViewsTests
{
    private static object View(string name, string status = "offline", string tag = "kids", string tab = "devices") =>
        new { name, tab, network = "192.168.30.0/24", status, guess = "", tag, query = "" };

    private static async Task<JsonElement> Views(HttpClient c) => (await c.GetFromJsonAsync<JsonElement>("/api/views")).GetProperty("views");

    [Fact]
    public async Task A_view_is_saved_listed_and_kept_with_everything_it_says()
    {
        using var app = new BamfApp();
        var c = app.Client();
        Assert.Equal(0, (await Views(c)).GetArrayLength());
        var r = await c.PostAsJsonAsync("/api/views", View("Kids offline"));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var v = (await Views(c))[0];
        Assert.Equal("Kids offline", v.GetProperty("name").GetString());
        Assert.Equal("offline", v.GetProperty("status").GetString());
        Assert.Equal("kids", v.GetProperty("tag").GetString());
        Assert.Equal("192.168.30.0/24", v.GetProperty("network").GetString());
    }

    [Fact]
    public async Task Saving_under_a_name_that_exists_replaces_it_in_any_case()
    {
        using var app = new BamfApp();
        var c = app.Client();
        await c.PostAsJsonAsync("/api/views", View("Kids offline", "offline"));
        await c.PostAsJsonAsync("/api/views", View("KIDS OFFLINE", "online"));
        var views = await Views(c);
        Assert.Equal(1, views.GetArrayLength());
        Assert.Equal("online", views[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_view_can_be_deleted_and_a_missing_one_is_a_404()
    {
        using var app = new BamfApp();
        var c = app.Client();
        await c.PostAsJsonAsync("/api/views", View("One"));
        await c.PostAsJsonAsync("/api/views", View("Two"));
        Assert.True((await c.PostAsJsonAsync("/api/views/delete", new { name = "one" })).IsSuccessStatusCode);
        Assert.Equal(["Two"], (await Views(c)).EnumerateArray().Select(v => v.GetProperty("name").GetString()).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/views/delete", new { name = "nope" })).StatusCode);
    }

    [Fact]
    public async Task There_is_a_limit_on_how_many_and_a_replaced_one_does_not_count_twice()
    {
        using var app = new BamfApp();
        var c = app.Client();
        for (var i = 0; i < HostStore.MaxViews; i++) Assert.True((await c.PostAsJsonAsync("/api/views", View("View " + i))).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/views", View("One too many"))).StatusCode);
        Assert.True((await c.PostAsJsonAsync("/api/views", View("View 3", "online"))).IsSuccessStatusCode);
        Assert.Equal(HostStore.MaxViews, (await Views(c)).GetArrayLength());
    }

    [Theory]
    [InlineData("", "devices", "all")]                       // no name
    [InlineData("Map view", "map", "all")]                   // not a tab a view can be on
    [InlineData("Odd status", "devices", "dancing")]
    public async Task A_view_that_makes_no_sense_is_refused(string name, string tab, string status)
    {
        using var app = new BamfApp();
        var r = await app.Client().PostAsJsonAsync("/api/views", new { name, tab, network = "all", status, guess = "", tag = "", query = "" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public void Names_and_fields_are_limited_and_control_characters_are_refused()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        Assert.NotNull(store.SaveView(new HostStore.SavedView(new string('x', HostStore.MaxViewName + 1), "devices", "all", "all", "", "", "")));
        Assert.NotNull(store.SaveView(new HostStore.SavedView("ok", "devices", "all", "all", "", "", new string('q', 101))));
        Assert.NotNull(store.SaveView(new HostStore.SavedView("ok", "devices", "all", "all", "bad\u0000guess", "", "")));
        Assert.Null(store.SaveView(new HostStore.SavedView("ok", "devices", "all", "all", "__none__", "", "*.245")));
        Assert.Single(store.GetViews());
    }

    [Fact]
    public void Blank_fields_mean_everything_and_the_forgotten_tab_is_allowed()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        Assert.Null(store.SaveView(new HostStore.SavedView("Forgotten ones", "forgotten", "", "", "", "", "")));
        var v = store.GetViews().Single();
        Assert.Equal(("forgotten", "all", "all"), (v.Tab, v.Network, v.Status));
    }
}
