using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>One change to several devices: each action does what the single-device one does, a missing device or a refused tag doesn't stop the rest, and it can be switched off.</summary>
[Collection("Endpoints")]
public class BulkTests
{
    private static long Add(HostStore store, int n)
    {
        var mac = $"bb:00:00:00:00:{n:x2}";
        store.UpsertSeen(mac, $"192.168.30.{100 + n}", "dev" + n, "Acme", "192.168.30.0/24");
        return store.GetAll().Single(h => h.Mac == mac).Id;
    }

    private static Task<HttpResponseMessage> Post(HttpClient c, object body) => c.PostAsJsonAsync("/api/hosts/bulk", body);

    [Fact]
    public async Task The_simple_actions_each_change_every_device_picked_and_only_those()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var c = app.Client();
        var a = Add(store, 1); var b = Add(store, 2); var other = Add(store, 3);
        var ids = new[] { a, b };

        async Task Do(string action) { var r = await Post(c, new { ids, action }); Assert.True(r.IsSuccessStatusCode, action + ": " + await r.Content.ReadAsStringAsync()); }
        HostRecord Get(long id) => store.GetAll().Single(h => h.Id == id);

        await Do("known"); Assert.True(Get(a).Known && Get(b).Known); Assert.False(Get(other).Known);
        await Do("unknown"); Assert.False(Get(a).Known);
        await Do("watch"); Assert.True(Get(a).Watched && Get(b).Watched); Assert.False(Get(other).Watched);
        await Do("unwatch"); Assert.False(Get(a).Watched);
        await Do("ignore"); Assert.True(Get(a).Ignored && Get(b).Ignored); Assert.False(Get(other).Ignored);
        await Do("unignore"); Assert.False(Get(a).Ignored);
        await Do("forget"); Assert.True(Get(a).Forgotten && Get(b).Forgotten); Assert.False(Get(other).Forgotten);
        await Do("restore"); Assert.False(Get(a).Forgotten && Get(b).Forgotten);
    }

    [Fact]
    public async Task Tags_are_added_once_kept_alongside_the_others_and_removed_by_name()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var c = app.Client();
        var a = Add(store, 4); var b = Add(store, 5);
        store.SetTags(a, ["kids"]);

        await Post(c, new { ids = new[] { a, b }, action = "tag", tag = "guest" });
        await Post(c, new { ids = new[] { a, b }, action = "tag", tag = "Guest" });      // the same tag again, in other case
        var tags = store.GetTags();
        Assert.Equal(["kids", "guest"], tags[a]);
        Assert.Equal(["guest"], tags[b]);

        await Post(c, new { ids = new[] { a, b }, action = "untag", tag = "GUEST" });
        tags = store.GetTags();
        Assert.Equal(["kids"], tags[a]);
        Assert.False(tags.TryGetValue(b, out var left) && left.Count > 0);
    }

    [Fact]
    public async Task Snooze_holds_alerts_for_each_and_unsnooze_ends_it()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var c = app.Client();
        var a = Add(store, 6); var b = Add(store, 7);
        Assert.True((await Post(c, new { ids = new[] { a, b }, action = "snooze", minutes = 60 })).IsSuccessStatusCode);
        Assert.Equal(2, store.GetSnoozes().Keys.Count(k => k == a || k == b));
        Assert.True((await Post(c, new { ids = new[] { a, b }, action = "unsnooze" })).IsSuccessStatusCode);
        Assert.Empty(store.GetSnoozes().Keys.Where(k => k == a || k == b));
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, new { ids = new[] { a }, action = "snooze" })).StatusCode);                          // no time given
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, new { ids = new[] { a }, action = "snooze", minutes = HostStore.MaxSnoozeMinutes + 1 })).StatusCode);
    }

    [Fact]
    public async Task A_device_that_is_gone_is_counted_and_the_rest_are_still_done()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var c = app.Client();
        var a = Add(store, 8);
        var j = await (await Post(c, new { ids = new[] { a, 999_999L }, action = "known" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, j.GetProperty("done").GetInt32());
        Assert.Equal(1, j.GetProperty("missing").GetInt32());
        Assert.True(store.GetAll().Single(h => h.Id == a).Known);
    }

    [Fact]
    public async Task A_tag_that_cant_be_added_is_reported_and_the_others_still_get_it()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var c = app.Client();
        var full = Add(store, 9); var fine = Add(store, 10);
        store.SetTags(full, Enumerable.Range(0, HostStore.MaxTagsPerHost).Select(i => "t" + i));
        var j = await (await Post(c, new { ids = new[] { full, fine }, action = "tag", tag = "new" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, j.GetProperty("done").GetInt32());
        Assert.Equal(full, j.GetProperty("failed")[0].GetProperty("id").GetInt64());
        Assert.Contains("new", store.GetTags()[fine]);
    }

    [Theory]
    [InlineData("""{"ids":[],"action":"known"}""")]
    [InlineData("""{"ids":[1],"action":"explode"}""")]
    [InlineData("""{"ids":[1],"action":"tag"}""")]
    [InlineData("""{"ids":[1],"action":"tag","tag":"a,b"}""")]
    public async Task A_request_that_makes_no_sense_is_refused(string json)
    {
        using var app = new BamfApp();
        var r = await app.Client().PostAsync("/api/hosts/bulk", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Too_many_at_once_is_refused()
    {
        using var app = new BamfApp();
        var ids = Enumerable.Range(1, HostStore.MaxBulkDevices + 1).Select(i => (long)i).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(app.Client(), new { ids, action = "known" })).StatusCode);
    }

    [Fact]
    public async Task Switched_off_it_refuses_and_the_dashboard_is_told()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var c = app.Client();
        var a = Add(store, 11);
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/hosts")).GetProperty("bulkEnabled").GetBoolean());
        Assert.True((await c.PostAsJsonAsync("/api/settings/bulk", new { enabled = false })).IsSuccessStatusCode);
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/hosts")).GetProperty("bulkEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await Post(c, new { ids = new[] { a }, action = "known" })).StatusCode);
        Assert.False(store.GetAll().Single(h => h.Id == a).Known);
        await c.PostAsJsonAsync("/api/settings/bulk", new { enabled = true });
        Assert.True((await Post(c, new { ids = new[] { a }, action = "known" })).IsSuccessStatusCode);
    }
}
