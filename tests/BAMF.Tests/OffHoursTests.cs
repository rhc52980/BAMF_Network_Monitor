using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>
/// Off hours: the plain rules for which moments are inside a window (across midnight, on chosen days), what is refused, and the once-a-night
/// alert for a device found on in them: not for one that is off, snoozed, ignored or without hours, and with its switch.
/// </summary>
[Collection("Endpoints")]
public class OffHoursTests
{
    // 2026-11-02 is a Monday.
    private static DateTime Mon(int h, int m = 0) => new(2026, 11, 2, h, m, 0);
    private static DateTime Day(int add, int h, int m = 0) => Mon(h, m).AddDays(add);

    [Fact]
    public void A_window_in_one_day_is_inside_between_its_ends_and_not_at_the_end()
    {
        var w = new OffWindow("13:00", "15:30", OffHours.AllDays);
        Assert.False(OffHours.Within(w, Mon(12, 59)));
        Assert.True(OffHours.Within(w, Mon(13, 0)));
        Assert.True(OffHours.Within(w, Mon(15, 29)));
        Assert.False(OffHours.Within(w, Mon(15, 30)));
    }

    [Fact]
    public void A_window_across_midnight_covers_the_evening_and_the_next_morning()
    {
        var w = new OffWindow("23:00", "07:00", OffHours.AllDays);
        Assert.True(OffHours.Within(w, Mon(23, 0)));
        Assert.True(OffHours.Within(w, Day(1, 3, 14)));
        Assert.True(OffHours.Within(w, Day(1, 6, 59)));
        Assert.False(OffHours.Within(w, Day(1, 7, 0)));
        Assert.False(OffHours.Within(w, Mon(12, 0)));
        Assert.Equal(Mon(0).Date, OffHours.StartDate(w, Mon(23, 30)));
        Assert.Equal(Mon(0).Date, OffHours.StartDate(w, Day(1, 3, 0)));                     // the small hours belong to the night that began the day before
    }

    [Fact]
    public void Days_are_the_days_a_window_starts_on_so_friday_night_runs_into_saturday_morning()
    {
        var fridayOnly = new OffWindow("23:00", "07:00", 1 << 4);                           // Friday
        Assert.True(OffHours.Within(fridayOnly, Day(4, 23, 30)));                           // Friday evening
        Assert.True(OffHours.Within(fridayOnly, Day(5, 2, 0)));                             // Saturday small hours: still Friday's window
        Assert.False(OffHours.Within(fridayOnly, Day(5, 23, 30)));                          // Saturday evening: not started on a chosen day
        Assert.False(OffHours.Within(fridayOnly, Day(4, 2, 0)));                            // Friday small hours belong to Thursday's
        var weekdays = new OffWindow("22:00", "06:00", 31);
        Assert.False(OffHours.Within(weekdays, Day(6, 1, 0)));                     // Sunday 01:00 follows Saturday night
        Assert.True(OffHours.Within(weekdays, Day(0, 22, 30)));
    }

    [Theory]
    [InlineData("23:00", "07:00", 127, null)]
    [InlineData("7:00", "07:00", 127, "hours and minutes")]
    [InlineData("25:00", "07:00", 127, "hours and minutes")]
    [InlineData("23:00", "23:00", 127, "same time")]
    [InlineData("23:00", "07:00", 0, "at least one day")]
    [InlineData("23:00", "07:00", 128, "at least one day")]
    public void A_window_that_cannot_work_is_refused_in_words(string from, string to, int days, string? contains)
    {
        var p = OffHours.Problem(from, to, days);
        if (contains is null) Assert.Null(p); else Assert.Contains(contains, p);
    }

    [Fact]
    public void A_window_reads_as_words()
    {
        Assert.Equal("23:00 to 07:00, every day", OffHours.Describe(new("23:00", "07:00", 127)));
        Assert.Equal("22:00 to 06:00, Monday to Friday", OffHours.Describe(new("22:00", "06:00", 31)));
        Assert.Equal("22:00 to 06:00, at weekends", OffHours.Describe(new("22:00", "06:00", 96)));
        Assert.Equal("22:00 to 06:00, on Mon, Wed", OffHours.Describe(new("22:00", "06:00", 5)));
    }

    private static long Seen(HostStore s, string mac, string name) { s.UpsertSeen(mac, "192.168.30.77", name, "Samsung", "192.168.30.0/24"); return s.GetAll().Single(h => h.Mac == mac).Id; }
    private static List<HostStore.AlertRow> Mine(HostStore s) => s.GetAlerts().Where(a => a.Kind == "schedule").ToList();

    [Fact]
    public async Task A_device_found_on_in_its_off_hours_alerts_once_for_the_night_and_again_the_next()
    {
        using var app = new BamfApp();
        var s = app.Services.GetRequiredService<HostStore>();
        var scanner = app.Services.GetRequiredService<ScannerService>();
        var id = Seen(s, "aa:00:00:00:70:01", "living-room-tv");
        Assert.Null(s.SetOffHours(id, new OffWindow("23:00", "07:00", OffHours.AllDays)));

        await scanner.RaiseOffHours(CancellationToken.None, Mon(12, 0));
        Assert.Empty(Mine(s));                                                              // on at noon: fine
        await scanner.RaiseOffHours(CancellationToken.None, Day(1, 3, 12));
        var a = Assert.Single(Mine(s));
        Assert.Contains("living-room-tv is on in its off hours", a.Title);
        Assert.Contains("03:12", a.Detail);
        Assert.Contains("23:00 to 07:00, every day", a.Detail);
        await scanner.RaiseOffHours(CancellationToken.None, Day(1, 3, 17));                  // the next scan, same night
        Assert.Single(Mine(s));
        await scanner.RaiseOffHours(CancellationToken.None, Day(1, 23, 40));                 // the next night
        Assert.Equal(2, Mine(s).Count);
    }

    [Fact]
    public async Task A_device_that_is_off_snoozed_ignored_or_has_no_hours_is_left_alone_and_the_switch_stops_it()
    {
        using var app = new BamfApp();
        var s = app.Services.GetRequiredService<HostStore>();
        var scanner = app.Services.GetRequiredService<ScannerService>();
        var night = Day(1, 3, 0);
        var w = new OffWindow("23:00", "07:00", OffHours.AllDays);

        var off = Seen(s, "aa:00:00:00:70:02", "offline-tv");
        s.MarkOffline(new HashSet<string>(), new[] { "192.168.30.0/24" }, 1);               // everything goes offline
        s.SetOffHours(off, w);
        var snoozed = Seen(s, "aa:00:00:00:70:03", "snoozed-tv"); s.SetOffHours(snoozed, w); s.SnoozeHost(snoozed, DateTime.UtcNow.AddHours(2), true);
        var ignored = Seen(s, "aa:00:00:00:70:04", "ignored-tv"); s.SetOffHours(ignored, w); s.SetIgnored(ignored, true);
        Seen(s, "aa:00:00:00:70:05", "no-hours-tv");
        await scanner.RaiseOffHours(CancellationToken.None, night);
        Assert.Empty(Mine(s));

        var live = Seen(s, "aa:00:00:00:70:06", "switched-off-tv"); s.SetOffHours(live, w);
        s.SetSetting("offHoursAlert", "false");
        await scanner.RaiseOffHours(CancellationToken.None, night);
        Assert.Empty(Mine(s));
        s.SetSetting("offHoursAlert", "true");
        await scanner.RaiseOffHours(CancellationToken.None, night);
        Assert.Contains("switched-off-tv", Assert.Single(Mine(s)).Title);
    }

    [Fact]
    public async Task Hours_are_set_and_cleared_over_the_api_and_a_bad_window_is_refused()
    {
        using var app = new BamfApp();
        var s = app.Services.GetRequiredService<HostStore>();
        var c = app.Client();
        var id = Seen(s, "aa:00:00:00:70:07", "kids-tablet");

        var bad = await c.PostAsJsonAsync($"/api/hosts/{id}/off-hours", new { from = "23:00", to = "23:00" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/hosts/99999/off-hours", new { from = "22:00", to = "06:00" })).StatusCode);

        Assert.True((await c.PostAsJsonAsync($"/api/hosts/{id}/off-hours", new { from = "21:00", to = "07:00", days = 31 })).IsSuccessStatusCode);
        var host = (await c.GetFromJsonAsync<JsonElement>("/api/hosts")).GetProperty("hosts").EnumerateArray().Single(h => h.GetProperty("id").GetInt64() == id);
        Assert.Equal("21:00 to 07:00, Monday to Friday", host.GetProperty("offHours").GetProperty("text").GetString());
        var list = await c.GetFromJsonAsync<JsonElement>("/api/off-hours");
        Assert.True(list.GetProperty("enabled").GetBoolean());
        Assert.Equal(id, Assert.Single(list.GetProperty("devices").EnumerateArray()).GetProperty("hostId").GetInt64());

        Assert.True((await c.PostAsJsonAsync($"/api/hosts/{id}/off-hours", new { from = "" })).IsSuccessStatusCode);          // cleared
        Assert.Empty(s.GetOffHours());

        Assert.True((await c.PostAsJsonAsync("/api/settings/off-hours-alert", new { enabled = false })).IsSuccessStatusCode);
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("editable").GetProperty("offHoursAlert").GetBoolean());
        Assert.Contains("offHoursAlert", HostStore.TransferKeys);
    }
}
