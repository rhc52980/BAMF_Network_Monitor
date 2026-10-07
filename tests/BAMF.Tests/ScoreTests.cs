using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>The network score and the daily all-quiet note: the arithmetic, the wording, and the switches in BAMF.</summary>
[Collection("Endpoints")]
public class ScoreTests
{
    private static readonly HealthScore.Inputs Clean = new(0, 0, 0, 0, 0, 0, 0, false, false, 0, 0, 0, 0, false, false, false);

    [Fact]
    public void A_clean_network_scores_100()
    {
        var s = HealthScore.Compute(Clean);
        Assert.Equal(100, s.Value);
        Assert.Equal("Healthy", s.Word);
        Assert.Empty(s.Reasons);
    }

    [Fact]
    public void Each_thing_takes_its_points_and_the_biggest_comes_first()
    {
        var s = HealthScore.Compute(Clean with { UnknownOnline = 2, Telnet = 1, Outages = 1, NoDestination = true });
        Assert.Equal(100 - 6 - 8 - 3 - 5, s.Value);
        Assert.Equal("Fine", s.Word);
        Assert.Equal(new[] { "Telnet is open on a device", "2 unknown devices are online", "Nobody would hear an alert: no destination is set up", "An internet outage this week" },
            s.Reasons.Select(r => r.Text));
    }

    [Fact]
    public void One_bad_category_is_capped()
    {
        var s = HealthScore.Compute(Clean with { UnknownOnline = 40 });
        Assert.Equal(85, s.Value);                    // 3 each, capped at 15
        Assert.Equal("40 unknown devices are online", Assert.Single(s.Reasons).Text);
        Assert.Equal(15, s.Reasons[0].Points);
    }

    [Fact]
    public void The_words_follow_the_number_and_the_floor_is_zero()
    {
        Assert.Equal("Healthy", HealthScore.WordFor(90));
        Assert.Equal("Fine", HealthScore.WordFor(89));
        Assert.Equal("Needs attention", HealthScore.WordFor(50));
        Assert.Equal("In trouble", HealthScore.WordFor(49));
        var s = HealthScore.Compute(Clean with { Noise = true, SecurityAlerts = 10, Telnet = 5, Upnp = true, CertsExpired = 9, WatchedDown = 9, DiskLow = true, NoDestination = true, Ftp = 9, Vnc = 9, UnknownOnline = 9, UnusualOpen = 9, Outages = 9, InternetDown = true });
        Assert.Equal(0, s.Value);
        Assert.Equal("In trouble", s.Word);
    }

    [Fact]
    public async Task The_score_and_its_history_answer_and_the_switch_round_trips()
    {
        using var app = new BamfApp();
        var score = app.Services.GetRequiredService<HealthScore>();
        score.Record(50, new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc));       // dropped once a month has passed
        score.Record(80, new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc));
        score.Record(70, new DateTime(2026, 10, 5, 15, 0, 0, DateTimeKind.Utc));     // the same day again: replaced
        score.Record(90, new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc));
        var c = app.Client();
        var d = await c.GetFromJsonAsync<JsonElement>("/api/score");
        Assert.True(d.GetProperty("enabled").GetBoolean());
        Assert.InRange(d.GetProperty("value").GetInt32(), 0, 100);
        // A fresh BAMF has no destination: that is one reason it isn't 100.
        Assert.Contains(d.GetProperty("reasons").EnumerateArray(), r => r.GetProperty("text").GetString()!.StartsWith("Nobody would hear"));
        var hist = d.GetProperty("history").EnumerateArray().Select(h => (h.GetProperty("date").GetString(), h.GetProperty("value").GetInt32())).ToList();
        Assert.Equal(new[] { ("2026-10-05", 70), ("2026-10-06", 90) }, hist.Where(h => h.Item1!.StartsWith("2026-10-0")));
        Assert.DoesNotContain(hist, h => h.Item1 == "2026-08-01");

        var off = await c.PostAsJsonAsync("/api/settings/score", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/score")).GetProperty("enabled").GetBoolean());
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("editable").GetProperty("networkScore").GetBoolean());
    }

    [Fact]
    public async Task A_quiet_day_says_so_and_a_day_with_alerts_lists_them()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var note = app.Services.GetRequiredService<DailyNote>();
        var (title, text, fields, quiet) = note.Compose();
        Assert.True(quiet);
        Assert.StartsWith("All quiet: ", title);
        Assert.Contains("No alerts in the last 24 hours.", text);
        Assert.Contains("Network score", text);
        Assert.Contains(fields, f => f.Name == "Alerts" && f.Value == "None in the last 24 hours");

        store.AddAlert("security", "garage-cam is scanning the network", "It reached 60 addresses in a minute.");
        store.AddAlert("port", "Port 23 opened on garage-cam", "Telnet.");
        (title, text, fields, quiet) = note.Compose();
        Assert.False(quiet);
        Assert.Equal("Yesterday: 2 alerts", title);
        Assert.Contains("Alerts in the last 24 hours: ", text);
        Assert.Contains("garage-cam is scanning the network", text);
        Assert.Contains("Port 23 opened on garage-cam", text);
        Assert.Contains(fields, f => f.Name == "Alerts (2)");

        var c = app.Client();
        var p = await c.GetFromJsonAsync<JsonElement>("/api/all-quiet/preview");
        Assert.Equal("Yesterday: 2 alerts", p.GetProperty("title").GetString());
        Assert.False(p.GetProperty("quiet").GetBoolean());
    }

    [Fact]
    public async Task The_daily_note_switch_and_hour_round_trip_and_sending_needs_a_destination()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var q = await c.GetFromJsonAsync<JsonElement>("/api/all-quiet");
        Assert.False(q.GetProperty("enabled").GetBoolean());
        Assert.Equal(8, q.GetProperty("hour").GetInt32());

        var bad = await c.PostAsJsonAsync("/api/settings/all-quiet", new { hour = 24 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var ok = await c.PostAsJsonAsync("/api/settings/all-quiet", new { enabled = true, hour = 7 });
        var d = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(d.GetProperty("enabled").GetBoolean());
        Assert.Equal(7, d.GetProperty("hour").GetInt32());
        Assert.False(string.IsNullOrEmpty(d.GetProperty("next").GetString()));
        var e = await c.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.True(e.GetProperty("editable").GetProperty("allQuiet").GetBoolean());
        Assert.Equal(7, e.GetProperty("editable").GetProperty("allQuietHour").GetInt32());

        var send = await c.PostAsync("/api/all-quiet/send", null);
        Assert.Equal(HttpStatusCode.BadRequest, send.StatusCode);
        Assert.Contains("No webhook is saved", (await send.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }
}
