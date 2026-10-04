using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;

namespace BAMF.Tests;

/// <summary>Tidying up old devices: only ones nobody has touched, only past the number of days, forgotten not deleted, and off until asked for.</summary>
[Collection("Endpoints")]
public class TidyTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    /// <summary>A device last seen <paramref name="daysAgo"/> days ago, offline.</summary>
    private static long Seen(HostStore store, string mac, int daysAgo)
    {
        store.UpsertSeen(mac, "192.168.30." + (Math.Abs(mac.GetHashCode()) % 200 + 20), "dev-" + mac[^2..], "Acme", "192.168.30.0/24");
        var id = store.GetAll().Single(h => h.Mac == mac).Id;
        using var conn = new SqliteConnection($"Data Source={store.DatabasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE hosts SET online = 0, last_seen = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$t", Now.AddDays(-daysAgo).ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        return id;
    }

    [Fact]
    public void A_device_untouched_and_gone_past_the_days_is_stale_and_a_recent_one_is_not()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var old = Seen(store, "aa:00:00:00:00:01", 90);
        Seen(store, "aa:00:00:00:00:02", 10);
        Assert.Equal([old], store.StaleDevices(60, Now).Select(h => h.Id));
        Assert.Equal(2, store.StaleDevices(5, Now).Count);
    }

    [Fact]
    public void Anything_you_did_to_a_device_keeps_it_out()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var known = Seen(store, "aa:00:00:00:01:01", 200); store.SetKnown(known, true);
        var watched = Seen(store, "aa:00:00:00:01:02", 200); store.SetWatched(watched, true);
        var ignored = Seen(store, "aa:00:00:00:01:03", 200); store.SetIgnored(ignored, true);
        var named = Seen(store, "aa:00:00:00:01:04", 200); store.SetName(named, "Dad's phone");
        var noted = Seen(store, "aa:00:00:00:01:05", 200); store.SetNote(noted, "guest");
        var tagged = Seen(store, "aa:00:00:00:01:06", 200); store.SetTags(tagged, ["guest"]);
        var typed = Seen(store, "aa:00:00:00:01:07", 200); store.SetDeviceType(typed, "phone");
        var untouched = Seen(store, "aa:00:00:00:01:08", 200);
        Assert.Equal([untouched], store.StaleDevices(60, Now).Select(h => h.Id));
    }

    [Fact]
    public void An_online_device_is_never_stale_whatever_its_last_seen_says()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var id = Seen(store, "aa:00:00:00:02:01", 300);
        store.UpsertSeen("aa:00:00:00:02:01", "192.168.30.99", "dev", "Acme", "192.168.30.0/24");    // seen again: online, last seen now
        Assert.Empty(store.StaleDevices(60, Now));
        Assert.NotEqual(0, id);
    }

    [Fact]
    public void Tidying_forgets_them_without_deleting_and_says_what_it_did()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var id = Seen(store, "aa:00:00:00:03:01", 100);
        var r = store.TidyStale(60, Now);
        Assert.Equal(1, r.Count);
        Assert.True(store.GetAll().Single(h => h.Id == id).Forgotten);        // still there, on the Forgotten tab
        Assert.Equal(1, store.LastTidy()!.Count);
        Assert.Empty(store.StaleDevices(60, Now));                             // and not tidied twice
        store.SetForgotten(id, false);
        Assert.False(store.GetAll().Single(h => h.Id == id).Forgotten);        // it can be brought back
    }

    [Fact]
    public async Task It_is_off_until_switched_on_the_days_are_limited_and_the_button_works_either_way()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var c = app.Client();
        Assert.False(store.TidyEnabled);
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/settings/tidy")).GetProperty("enabled").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/tidy", new { enabled = true, days = 13 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/tidy", new { enabled = true, days = 731 })).StatusCode);
        Assert.False(store.TidyEnabled);

        var id = Seen(store, "aa:00:00:00:04:01", 400);
        var j = await c.GetFromJsonAsync<JsonElement>("/api/settings/tidy");
        Assert.Equal(1, j.GetProperty("would").GetInt32());
        var run = await c.PostAsync("/api/tidy/run", null);
        Assert.True(run.IsSuccessStatusCode);
        Assert.True(store.GetAll().Single(h => h.Id == id).Forgotten);
        Assert.False(store.TidyEnabled);          // the button did not switch the daily run on

        Assert.True((await c.PostAsJsonAsync("/api/settings/tidy", new { enabled = true, days = 90 })).IsSuccessStatusCode);
        Assert.True(store.TidyEnabled);
        Assert.Equal(90, store.TidyDays);
    }
}
