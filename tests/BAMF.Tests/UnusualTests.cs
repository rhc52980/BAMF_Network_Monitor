using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>
/// What's normal for a device, and what isn't: the three things BAMF notices,
/// on made-up histories, and the whole watch in BAMF itself.
/// </summary>
public class UnusualTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 14, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static Unusual.History Device(int ageDays, bool online, params (double DaysAgo, bool Online)[] events) =>
        new(1, "garage-cam", Now.AddDays(-ageDays), online, false,
            events.Select(e => (Now.AddDays(-e.DaysAgo), e.Online)).OrderBy(e => e.Item1).ToList());

    // ---------- off longer than usual ----------

    [Fact]
    public void An_always_on_device_off_far_longer_than_ever_is_noticed()
    {
        // On for a month, one five-minute blip ten days ago, off for the last 47 minutes.
        var d = Device(40, false, (40, true), (10, false), (10 - 5.0 / 1440, true), (47.0 / 1440, false));
        var f = Unusual.OffTooLong(d, Now);
        Assert.NotNull(f);
        Assert.Equal("garage-cam has been off for 47 minutes", f.Title);
        Assert.Contains("never off for more than 5 minutes", f.Detail);
        Assert.True(f.Ongoing);
    }

    [Theory]
    [InlineData(10)]    // under half an hour
    [InlineData(29)]
    public void A_short_break_is_not_unusual(int minutesOff)
    {
        var d = Device(40, false, (40, true), (minutesOff / 1440.0, false));
        Assert.Null(Unusual.OffTooLong(d, Now));
    }

    [Fact]
    public void Off_for_less_than_three_times_its_longest_break_is_not_unusual()
    {
        // A two-hour break last week; three hours off now is under three times that.
        var d = Device(40, false, (40, true), (7, false), (7 - 2.0 / 24, true), (3.0 / 24, false));
        Assert.Null(Unusual.OffTooLong(d, Now));
    }

    [Fact]
    public void A_device_that_comes_and_goes_is_not_expected_to_stay_on()
    {
        var events = new List<(double, bool)>();
        for (var day = 30; day >= 1; day--) { events.Add((day, true)); events.Add((day - 0.5, false)); }
        events.Add((0.2, false));
        Assert.Null(Unusual.OffTooLong(Device(40, false, events.ToArray()), Now));
    }

    [Fact]
    public void A_device_off_since_the_window_began_is_not_called_always_on()
    {
        // Seen for 40 days, but off for the last 25: only 3 days of it on in the window.
        // (Found replaying a real network, where it read "off 27 days; it's almost always on".)
        var d = Device(40, false, (40, true), (25, false));
        Assert.Null(Unusual.OffTooLong(d, Now));
    }

    [Fact]
    public void One_minute_is_one_minute()
    {
        var d = Device(40, false, (40, true), (10, false), (10 - 1.0 / 1440, true), (47.0 / 1440, false));
        Assert.Contains("never off for more than 1 minute.", Unusual.OffTooLong(d, Now)!.Detail);
    }

    [Fact]
    public void Nothing_is_said_in_the_first_two_weeks()
    {
        var d = Device(10, false, (10, true), (1, false));
        Assert.Null(Unusual.OffTooLong(d, Now));
    }

    [Fact]
    public void A_watched_device_is_left_to_its_own_alert()
    {
        var d = Device(40, false, (40, true), (0.1, false)) with { Watched = true };
        Assert.Null(Unusual.OffTooLong(d, Now));
    }

    // ---------- on at an unusual hour ----------

    /// <summary>On every evening, 18:00 to 23:00, for four weeks; today it came on at the given time.</summary>
    private static Unusual.History Evenings(DateTime cameOn)
    {
        var events = new List<(DateTime, bool)>();
        for (var day = 28; day >= 1; day--)
        {
            var d = Now.Date.AddDays(-day);
            events.Add((d.AddHours(18), true));
            events.Add((d.AddHours(23), false));
        }
        events.Add((cameOn, true));
        return new Unusual.History(2, "living-room-tv", Now.AddDays(-40), true, false, events);
    }

    [Fact]
    public void Coming_on_at_an_hour_it_never_is_is_noticed()
    {
        var f = Unusual.OddHour(Evenings(Now.Date.AddHours(3).AddMinutes(12)), Now.Date.AddHours(3).AddMinutes(20), Utc);
        Assert.NotNull(f);
        Assert.Equal("living-room-tv came online at 3:12 am", f.Title);
        Assert.Contains("never on between 2 am and 5 am", f.Detail);
        Assert.False(f.Ongoing);
    }

    [Fact]
    public void Coming_on_at_odd_hours_twice_in_a_day_is_one_note()
    {
        var first = Unusual.OddHour(Evenings(Now.Date.AddHours(3).AddMinutes(12)), Now.Date.AddHours(3).AddMinutes(20), Utc)!;
        var again = Unusual.OddHour(Evenings(Now.Date.AddHours(4).AddMinutes(30)), Now.Date.AddHours(4).AddMinutes(35), Utc)!;
        Assert.Equal(first.Key, again.Key);
    }

    [Fact]
    public void Coming_on_at_its_usual_time_is_not()
    {
        Assert.Null(Unusual.OddHour(Evenings(Now.Date.AddHours(18).AddMinutes(5)), Now.Date.AddHours(18).AddMinutes(10), Utc));
    }

    [Fact]
    public void An_hour_next_to_its_usual_ones_is_not()
    {
        // On from 18:00 usually; 17:30 is within an hour of that.
        Assert.Null(Unusual.OddHour(Evenings(Now.Date.AddHours(17).AddMinutes(30)), Now.Date.AddHours(17).AddMinutes(35), Utc));
    }

    [Fact]
    public void Only_when_it_has_just_come_on()
    {
        Assert.Null(Unusual.OddHour(Evenings(Now.Date.AddHours(3)), Now.Date.AddHours(3).AddMinutes(45), Utc));
    }

    [Fact]
    public void A_device_that_is_almost_always_on_has_no_usual_hours()
    {
        var d = new Unusual.History(3, "nas", Now.AddDays(-40), true, false, [(Now.AddDays(-40), true), (Now.AddMinutes(-20), false), (Now.AddMinutes(-5), true)]);
        Assert.Null(Unusual.OddHour(d, Now, Utc));
    }

    // ---------- slower than usual ----------

    [Fact]
    public void Several_times_slower_than_usual_is_noticed()
    {
        var f = Unusual.Slow(4, "nas", 10, [60, 62, 58, 70, 65]);
        Assert.NotNull(f);
        Assert.Contains("62 ms, against a usual 10 ms", f.Detail);
    }

    [Theory]
    [InlineData(new[] { 30, 32, 28, 35, 31 })]   // three times: not enough
    [InlineData(new[] { 55, 58, 52, 60, 57 })]   // over four times but not 50 ms more: under 60
    public void A_little_slower_is_not(int[] lastFive)
    {
        Assert.Null(Unusual.Slow(4, "nas", 10, lastFive));
    }

    [Fact]
    public void Slow_is_over_once_it_is_back_under_twice_its_usual()
    {
        Assert.False(Unusual.SlowOver(10, [60, 62, 58, 70, 65]));
        Assert.True(Unusual.SlowOver(10, [15, 12, 18, 14, 16]));
    }

    // ---------- the watch, in BAMF itself ----------

    [Fact]
    public async Task The_watch_notes_it_once_alerts_on_it_and_lets_it_be_called_normal()
    {
        using var app = new BamfApp();
        var c = app.Client();
        await c.GetAsync("/api/hosts");   // BAMF up, its database made
        var now = DateTime.UtcNow;
        long id;
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(app.Dir, "bamf.db")};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO hosts (mac, ip, hostname, subnet, online, known, first_seen, last_seen)
                VALUES ('AA:BB:CC:00:00:01', '192.168.30.71', 'garage-cam', '192.168.30.0/24', 0, 1, $first, $last);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$first", now.AddDays(-40).ToString("o"));
            cmd.Parameters.AddWithValue("$last", now.AddHours(-2).ToString("o"));
            id = (long)cmd.ExecuteScalar()!;
            using var ev = conn.CreateCommand();
            ev.CommandText = "INSERT INTO events (host_id, type, at) VALUES ($h, 'online', $a), ($h, 'offline', $b)";
            ev.Parameters.AddWithValue("$h", id);
            ev.Parameters.AddWithValue("$a", now.AddDays(-40).ToString("o"));
            ev.Parameters.AddWithValue("$b", now.AddHours(-2).ToString("o"));
            ev.ExecuteNonQuery();
        }

        var watch = app.Services.GetRequiredService<UnusualWatch>();
        var first = await watch.Check(now, TimeZoneInfo.Utc, CancellationToken.None);
        var f = Assert.Single(first);
        Assert.Equal("garage-cam has been off for 2 h 0 min", f.Title);
        Assert.Empty(await watch.Check(now.AddMinutes(5), TimeZoneInfo.Utc, CancellationToken.None));   // once

        var list = await c.GetFromJsonAsync<JsonElement>("/api/unusual");
        var item = list.GetProperty("items").EnumerateArray().Single();
        Assert.True(item.GetProperty("open").GetBoolean());
        Assert.Equal("garage-cam has been off for 2 h 5 min", item.GetProperty("title").GetString());   // kept up to date

        var normal = await c.PostAsync($"/api/unusual/{item.GetProperty("id").GetInt64()}/normal", null);
        Assert.Equal(HttpStatusCode.OK, normal.StatusCode);
        var after = (await c.GetFromJsonAsync<JsonElement>("/api/unusual")).GetProperty("items")[0];
        Assert.False(after.GetProperty("open").GetBoolean());
        Assert.True(after.GetProperty("normal").GetBoolean());
        Assert.Empty(await watch.Check(now.AddMinutes(10), TimeZoneInfo.Utc, CancellationToken.None));   // muted
    }

    [Fact]
    public async Task It_can_be_switched_off_and_the_change_is_logged()
    {
        using var app = new BamfApp();
        var c = app.Client();
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/unusual")).GetProperty("enabled").GetBoolean());
        await c.PostAsJsonAsync("/api/settings/unusual", new { enabled = false });
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/unusual")).GetProperty("enabled").GetBoolean());
        Assert.Equal("Notice unusual activity", (await c.GetFromJsonAsync<JsonElement>("/api/settings/log"))[0].GetProperty("what").GetString());
    }
}
