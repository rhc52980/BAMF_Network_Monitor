using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>
/// What the 3D tab gets beyond the devices: scans said lately, where devices talk seen from the other end, and the last day's
/// events to replay.
/// </summary>
[Collection("Endpoints")]
public class More3dTests
{
    private static IPAddress Ip(string s) => IPAddress.Parse(s);
    private static long AddHost(HostStore store, int n)
    {
        var mac = $"ee:00:00:00:30:{n:x2}";
        store.UpsertSeen(mac, $"192.168.30.{150 + n}", "tab" + n, "Acme", "192.168.30.0/24");
        return store.GetAll().Single(h => h.Mac == mac).Id;
    }
    private static string MacOf(HostStore store, long id) => store.GetAll().Single(h => h.Id == id).Mac;

    private static void Sql(HostStore store, string sql, params (string, object)[] args)
    {
        using var conn = new SqliteConnection($"Data Source={store.DatabasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_scan_is_kept_for_a_while_for_the_3D_view_and_then_not()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var w = app.Services.GetRequiredService<FlowWatch>();
        var id = AddHost(store, 1);
        var mac = MacOf(store, id);
        var now = new DateTime(2026, 10, 8, 14, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= 40; i++) w.Observe(mac, Ip("192.168.30.151"), Ip($"192.168.30.{100 + i}"), 6, 445, 60, now.AddSeconds(i));
        Assert.Empty(w.RecentScans(now.AddMinutes(1)));                                  // noted, not yet said
        await w.Evaluate(CancellationToken.None, now.AddMinutes(1));
        var seen = Assert.Single(w.RecentScans(now.AddMinutes(2)));
        Assert.Equal("addresses", seen.What);
        Assert.Equal(40, seen.Count);
        Assert.Empty(w.RecentScans(now.AddMinutes(45)));                                 // over half an hour ago
        Assert.Single(w.RecentScans(now.AddMinutes(45), TimeSpan.FromHours(1)));

        var map = await app.Client().GetFromJsonAsync<JsonElement>("/api/flows/map");
        Assert.True(map.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Array, map.GetProperty("scans").ValueKind);
    }

    [Fact]
    public async Task Destinations_are_seen_from_the_other_end_and_new_ones_are_new_only_to_a_device_that_has_learned()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var a = AddHost(store, 2);
        var b = AddHost(store, 3);
        var now = DateTime.UtcNow;
        store.AddFlows(new[]
        {
            // A has been talking for ten days to one place, and started on another an hour ago
            new HostStore.FlowDelta(MacOf(store, a), "203.0.113.0/24", now.AddDays(-10), 5000, 50, "203.0.113.5"),
            new HostStore.FlowDelta(MacOf(store, a), "203.0.113.0/24", now.AddMinutes(-5), 100, 1, "203.0.113.5"),
            new HostStore.FlowDelta(MacOf(store, a), "198.51.100.0/24", now.AddHours(-1), 800, 4, "198.51.100.7"),
            // B only began today, so everything is new to it but it is still learning
            new HostStore.FlowDelta(MacOf(store, b), "198.51.100.0/24", now.AddHours(-2), 100, 1, "198.51.100.9"),
            new HostStore.FlowDelta(MacOf(store, b), "192.0.2.0/24", now.AddHours(-2), 100, 1, "192.0.2.4"),
        });
        var map = await app.Client().GetFromJsonAsync<JsonElement>("/api/flows/map");
        var dests = map.GetProperty("destinations").EnumerateArray().ToDictionary(d => d.GetProperty("net").GetString()!);
        Assert.Equal(3, dests.Count);
        Assert.False(dests["203.0.113.0/24"].GetProperty("isNew").GetBoolean());           // older than a day
        Assert.True(dests["198.51.100.0/24"].GetProperty("isNew").GetBoolean());            // new today, and A has learned its habits
        Assert.False(dests["192.0.2.0/24"].GetProperty("isNew").GetBoolean());              // only B, which is still learning
        var both = dests["198.51.100.0/24"].GetProperty("devices").EnumerateArray().Select(x => x.GetInt64()).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { a, b }.OrderBy(x => x).ToArray(), both);
        Assert.Equal(5100, dests["203.0.113.0/24"].GetProperty("bytes").GetInt64());
        Assert.Equal("203.0.113.5", dests["203.0.113.0/24"].GetProperty("sample").GetString());
    }

    [Fact]
    public async Task The_timeline_says_who_was_online_at_the_start_and_what_happened_after()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var up = AddHost(store, 4);        // goes offline during the day, so it was online at the start
        var down = AddHost(store, 5);      // comes online during the day, so it was offline at the start
        var quiet = AddHost(store, 6);     // only the event of being first seen, so it wasn't there at the start
        var now = DateTime.UtcNow;
        Sql(store, "INSERT INTO events (host_id, type, at) VALUES ($h, 'offline', $at)", ("$h", up), ("$at", now.AddHours(-5).ToString("o")));
        Sql(store, "INSERT INTO events (host_id, type, at) VALUES ($h, 'online', $at)", ("$h", up), ("$at", now.AddHours(-3).ToString("o")));
        Sql(store, "INSERT INTO events (host_id, type, at) VALUES ($h, 'online', $at)", ("$h", down), ("$at", now.AddHours(-9).ToString("o")));
        Sql(store, "INSERT INTO events (host_id, type, at) VALUES ($h, 'offline', $at)", ("$h", quiet), ("$at", now.AddDays(-3).ToString("o")));       // before the window
        store.AddWanOutage(now.AddHours(-4), now.AddHours(-4).AddMinutes(12), false);

        var tl = await app.Client().GetFromJsonAsync<JsonElement>("/api/timeline?hours=24");
        var hosts = tl.GetProperty("hosts").EnumerateArray().ToDictionary(h => h.GetProperty("id").GetInt64());
        Assert.True(hosts[up].GetProperty("online0").GetBoolean());
        Assert.False(hosts[down].GetProperty("online0").GetBoolean());
        Assert.False(hosts[quiet].GetProperty("online0").GetBoolean());                                                     // it arrived during the day
        var events = tl.GetProperty("events").EnumerateArray().Where(e => DateTime.Parse(e.GetProperty("at").GetString()!).ToUniversalTime() < now.AddHours(-1))
            .Select(e => (e.GetProperty("h").GetInt64(), e.GetProperty("type").GetString()!)).ToList();
        Assert.Equal(new[] { (down, "online"), (up, "offline"), (up, "online") }, events);      // oldest first, nothing from before the window
        Assert.Equal(1, tl.GetProperty("outages").GetArrayLength());
        Assert.True(DateTime.Parse(tl.GetProperty("to").GetString()!) > DateTime.Parse(tl.GetProperty("from").GetString()!));

        // The hours asked for are bounded.
        var wide = await app.Client().GetFromJsonAsync<JsonElement>("/api/timeline?hours=9999");
        Assert.True((DateTime.Parse(wide.GetProperty("to").GetString()!) - DateTime.Parse(wide.GetProperty("from").GetString()!)).TotalHours <= 72.01);
    }
}
