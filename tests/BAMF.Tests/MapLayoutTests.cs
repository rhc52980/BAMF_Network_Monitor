using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BAMF.Tests;

/// <summary>Named layouts for a Map: kept per map under a name, replaced by the same name, refused when they can't be kept, and deleted.</summary>
[Collection("Endpoints")]
public class MapLayoutTests
{
    private const string Net = "192.168.30.0/24";
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static object Body(string name, string subnet = Net, double x = 10) =>
        new { subnet, name, positions = new Dictionary<string, double[]> { ["h:1"] = new[] { x, 20 }, ["gw"] = new[] { 0.0, 0 } } };

    [Fact]
    public async Task A_layout_is_kept_under_its_name_for_its_own_map_and_listed()
    {
        using var app = new BamfApp();
        var c = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/map/layouts", Body("Wide"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/map/layouts", Body("Tall", x: 55))).StatusCode);
        await c.PostAsJsonAsync("/api/map/layouts", Body("Elsewhere", "192.168.20.0/24"));

        var list = await c.GetFromJsonAsync<JsonElement>("/api/map/layouts?subnet=" + Uri.EscapeDataString(Net));
        Assert.Equal(new[] { "Wide", "Tall" }, list.EnumerateArray().Select(l => l.GetProperty("name").GetString()).ToArray());
        Assert.Equal(2, list[0].GetProperty("nodes").GetInt32());
        Assert.Equal(55, list[1].GetProperty("positions").GetProperty("h:1")[0].GetDouble());
        var other = await c.GetFromJsonAsync<JsonElement>("/api/map/layouts?subnet=" + Uri.EscapeDataString("192.168.20.0/24"));
        Assert.Equal(1, other.GetArrayLength());                                                       // another map's layouts are its own
    }

    [Fact]
    public async Task Saving_under_a_name_that_is_taken_replaces_it_whatever_the_capitals_and_spaces()
    {
        using var app = new BamfApp();
        var c = app.Client();
        await c.PostAsJsonAsync("/api/map/layouts", Body("Rack  view"));
        await c.PostAsJsonAsync("/api/map/layouts", Body("  rack view ", x: 99));
        var list = await c.GetFromJsonAsync<JsonElement>("/api/map/layouts?subnet=" + Uri.EscapeDataString(Net));
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("rack view", list[0].GetProperty("name").GetString());                            // the newest wording, spaces made single
        Assert.Equal(99, list[0].GetProperty("positions").GetProperty("h:1")[0].GetDouble());
    }

    [Fact]
    public async Task A_layout_that_cannot_be_kept_is_refused_and_nothing_is_saved()
    {
        using var app = new BamfApp();
        var c = app.Client();
        foreach (var body in new object[]
        {
            Body(""),
            Body(new string('x', 41)),
            Body("bad\u0007name"),
            Body("ok", ""),
            new { subnet = Net, name = "empty", positions = new Dictionary<string, double[]>() },
            new { subnet = Net, name = "key", positions = new Dictionary<string, double[]> { ["../x"] = new[] { 1.0, 2 } } },
            new { subnet = Net, name = "far", positions = new Dictionary<string, double[]> { ["h:1"] = new[] { 1e9, 2 } } },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/map/layouts", body)).StatusCode);
        var list = await c.GetFromJsonAsync<JsonElement>("/api/map/layouts?subnet=" + Uri.EscapeDataString(Net));
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task A_map_keeps_a_limited_number_but_the_same_name_can_always_be_updated()
    {
        using var app = new BamfApp();
        var c = app.Client();
        for (var i = 0; i < 30; i++) Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/map/layouts", Body("L" + i))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/map/layouts", Body("one too many"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/map/layouts", Body("l7", x: 3))).StatusCode);
    }

    [Fact]
    public async Task A_layout_can_be_deleted_and_deleting_one_that_is_not_there_says_so()
    {
        using var app = new BamfApp();
        var c = app.Client();
        await c.PostAsJsonAsync("/api/map/layouts", Body("Wide"));
        var url = "/api/map/layouts?subnet=" + Uri.EscapeDataString(Net) + "&name=";
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync(url + "Nope")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync(url + "wide")).StatusCode);
        var list = await c.GetFromJsonAsync<JsonElement>("/api/map/layouts?subnet=" + Uri.EscapeDataString(Net));
        Assert.Equal(0, list.GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/map/layouts")).StatusCode);   // which map?
    }
}
