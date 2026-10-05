using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>Undo for a bulk change: each device goes back to exactly how it stood, including the watch that forgetting drops.</summary>
[Collection("Endpoints")]
public class BulkUndoTests
{
    private static long Add(HostStore store, int n)
    {
        var mac = $"dd:00:00:00:00:{n:x2}";
        store.UpsertSeen(mac, $"192.168.30.{150 + n}", "dev" + n, "Acme", "192.168.30.0/24");
        return store.GetAll().Single(h => h.Mac == mac).Id;
    }

    private static HostStore.HostState Snapshot(HostStore store, long id) =>
        store.GetAll().Where(h => h.Id == id).Select(h => new HostStore.HostState(h.Id, h.Known, h.Watched, h.Ignored, h.Forgotten,
            store.GetTags().TryGetValue(id, out var t) ? t : new List<string>(), store.GetSnoozes().TryGetValue(id, out var s) ? s : null)).Single();

    [Fact]
    public async Task Forgetting_a_watched_device_and_undoing_it_brings_the_watch_back()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var c = app.Client();
        var a = Add(store, 1); var b = Add(store, 2);
        store.SetWatched(a, true); store.SetKnown(a, true); store.SetTags(a, ["kids", "iot"]);
        var before = new[] { Snapshot(store, a), Snapshot(store, b) };

        await c.PostAsJsonAsync("/api/hosts/bulk", new { ids = new[] { a, b }, action = "forget" });
        Assert.True(store.GetAll().Single(h => h.Id == a).Forgotten);
        Assert.False(store.GetAll().Single(h => h.Id == a).Watched);         // forgetting drops the watch

        var r = await c.PostAsJsonAsync("/api/hosts/bulk/restore", new { states = before });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var ha = store.GetAll().Single(h => h.Id == a);
        Assert.False(ha.Forgotten);
        Assert.True(ha.Watched && ha.Known);
        Assert.Equal(["kids", "iot"], store.GetTags()[a]);
        Assert.False(store.GetAll().Single(h => h.Id == b).Forgotten);
    }

    [Fact]
    public async Task Tags_known_ignored_and_snooze_all_go_back()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var c = app.Client();
        var a = Add(store, 3);
        store.SetKnown(a, true);
        var before = Snapshot(store, a);

        await c.PostAsJsonAsync("/api/hosts/bulk", new { ids = new[] { a }, action = "unknown" });
        await c.PostAsJsonAsync("/api/hosts/bulk", new { ids = new[] { a }, action = "ignore" });
        await c.PostAsJsonAsync("/api/hosts/bulk", new { ids = new[] { a }, action = "tag", tag = "temp" });
        await c.PostAsJsonAsync("/api/hosts/bulk", new { ids = new[] { a }, action = "snooze", minutes = 60 });
        Assert.NotEmpty(store.GetSnoozes().Where(kv => kv.Key == a));

        Assert.True((await c.PostAsJsonAsync("/api/hosts/bulk/restore", new { states = new[] { before } })).IsSuccessStatusCode);
        var h = store.GetAll().Single(x => x.Id == a);
        Assert.True(h.Known);
        Assert.False(h.Ignored);
        Assert.DoesNotContain(store.GetTags(), kv => kv.Key == a && kv.Value.Contains("temp"));
        Assert.DoesNotContain(store.GetSnoozes(), kv => kv.Key == a);
    }

    [Fact]
    public async Task A_snooze_that_was_running_is_put_back_with_its_time()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var c = app.Client();
        var a = Add(store, 4);
        store.SnoozeHost(a, DateTime.UtcNow.AddHours(2), true);
        var before = Snapshot(store, a);
        await c.PostAsJsonAsync("/api/hosts/bulk", new { ids = new[] { a }, action = "unsnooze" });
        Assert.DoesNotContain(store.GetSnoozes(), kv => kv.Key == a);
        await c.PostAsJsonAsync("/api/hosts/bulk/restore", new { states = new[] { before } });
        Assert.Contains(store.GetSnoozes(), kv => kv.Key == a);
    }

    [Fact]
    public async Task A_device_that_has_gone_is_counted_and_nothing_to_put_back_is_refused()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var c = app.Client();
        var a = Add(store, 5);
        var j = await (await c.PostAsJsonAsync("/api/hosts/bulk/restore", new { states = new[] { Snapshot(store, a), new HostStore.HostState(999_999, true, false, false, false, null, null) } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, j.GetProperty("done").GetInt32());
        Assert.Equal(1, j.GetProperty("missing").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/hosts/bulk/restore", new { states = Array.Empty<object>() })).StatusCode);
    }

    [Fact]
    public async Task Switched_off_with_Select_mode_it_refuses()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var c = app.Client();
        var a = Add(store, 6);
        await c.PostAsJsonAsync("/api/settings/bulk", new { enabled = false });
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/hosts/bulk/restore", new { states = new[] { Snapshot(store, a) } })).StatusCode);
    }
}
