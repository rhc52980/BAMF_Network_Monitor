using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>What each device has moved over the last hours, for the 3D tab's usage skyline, and a longer replay.</summary>
[Collection("Endpoints")]
public class Usage3dTests
{
    private static long AddHost(HostStore store, int n)
    {
        var mac = $"ee:00:00:00:40:{n:x2}";
        store.UpsertSeen(mac, $"192.168.30.{160 + n}", "usage" + n, "Acme", "192.168.30.0/24");
        return store.GetAll().Single(h => h.Mac == mac).Id;
    }
    private static string MacOf(HostStore store, long id) => store.GetAll().Single(h => h.Id == id).Mac;
    private static string Hour(DateTime t) => t.ToString("yyyy-MM-ddTHH:00:00Z");

    [Fact]
    public async Task Usage_adds_up_each_devices_hours_in_the_window_biggest_first_and_leaves_out_the_quiet()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var big = AddHost(store, 1);
        var small = AddHost(store, 2);
        AddHost(store, 3);                                                                             // moved nothing
        var now = DateTime.UtcNow;
        store.AddTraffic(new[]
        {
            (MacOf(store, big), Hour(now.AddHours(-1)), 4_000_000_000L, 1_000_000L),
            (MacOf(store, big), Hour(now.AddDays(-3)), 10_000_000_000L, 0L),                           // outside a day, inside a week
            (MacOf(store, small), Hour(now.AddHours(-2)), 500_000L, 2_000_000L),
        });
        var c = app.Client();

        var day = await c.GetFromJsonAsync<JsonElement>("/api/traffic/usage?hours=24");
        Assert.Equal(24, day.GetProperty("hours").GetInt32());
        var rows = day.GetProperty("devices").EnumerateArray().ToList();
        Assert.Equal(new[] { big, small }, rows.Select(r => r.GetProperty("hostId").GetInt64()).ToArray());
        Assert.Equal(4_000_000_000L, rows[0].GetProperty("rx").GetInt64());
        Assert.Equal(2_000_000L, rows[1].GetProperty("tx").GetInt64());

        var week = await c.GetFromJsonAsync<JsonElement>("/api/traffic/usage?hours=168");
        Assert.Equal(14_000_000_000L, week.GetProperty("devices")[0].GetProperty("rx").GetInt64());     // the three-day-old hour counts now

        var wide = await c.GetFromJsonAsync<JsonElement>("/api/traffic/usage?hours=99999");
        Assert.Equal(720, wide.GetProperty("hours").GetInt32());                                         // bounded
    }

    [Fact]
    public async Task A_machines_extra_cards_count_towards_it_and_forgotten_devices_are_left_out()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var main = AddHost(store, 4);
        var card = AddHost(store, 5);
        var gone = AddHost(store, 6);
        Assert.Null(store.SetInterfaceOf(card, main));
        store.SetForgotten(gone, true);
        var now = DateTime.UtcNow;
        store.AddTraffic(new[]
        {
            (MacOf(store, main), Hour(now.AddHours(-1)), 1_000L, 200L),
            (MacOf(store, card), Hour(now.AddHours(-1)), 3_000L, 400L),
            (MacOf(store, gone), Hour(now.AddHours(-1)), 9_000L, 9_000L),
        });
        var usage = await app.Client().GetFromJsonAsync<JsonElement>("/api/traffic/usage?hours=24");
        var row = Assert.Single(usage.GetProperty("devices").EnumerateArray());
        Assert.Equal(main, row.GetProperty("hostId").GetInt64());
        Assert.Equal(4_000L, row.GetProperty("rx").GetInt64());
        Assert.Equal(600L, row.GetProperty("tx").GetInt64());
    }

    [Fact]
    public async Task The_replay_can_go_back_thirty_days()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        AddHost(store, 7);
        var month = await app.Client().GetFromJsonAsync<JsonElement>("/api/timeline?hours=720");
        var span = DateTime.Parse(month.GetProperty("to").GetString()!) - DateTime.Parse(month.GetProperty("from").GetString()!);
        Assert.InRange(span.TotalDays, 29.99, 30.01);
    }
}
