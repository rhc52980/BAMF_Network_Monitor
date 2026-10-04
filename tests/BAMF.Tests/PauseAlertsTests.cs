using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>Pause alerts: it holds every alert like quiet hours do, ends by itself or on request, can be switched off, and is limited.</summary>
[Collection("Endpoints")]
public class PauseAlertsTests
{
    private static async Task<JsonElement> Hosts(HttpClient c) => await c.GetFromJsonAsync<JsonElement>("/api/hosts");

    [Fact]
    public async Task A_pause_is_on_until_its_time_and_the_dashboard_is_told()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var scanner = app.Services.GetRequiredService<ScannerService>();
        Assert.False(scanner.IsQuietNow());
        Assert.True((await Hosts(c)).GetProperty("pauseEnabled").GetBoolean());

        var r = await c.PostAsJsonAsync("/api/alerts/pause", new { minutes = 30 });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var until = DateTime.Parse((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pausedUntil").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.InRange((until.ToUniversalTime() - DateTime.UtcNow).TotalMinutes, 29, 30.1);
        Assert.True(scanner.IsQuietNow());
        Assert.Equal(JsonValueKind.String, (await Hosts(c)).GetProperty("alertsPausedUntil").ValueKind);

        await c.PostAsJsonAsync("/api/alerts/pause", new { minutes = 0 });
        Assert.False(scanner.IsQuietNow());
        Assert.Equal(JsonValueKind.Null, (await Hosts(c)).GetProperty("alertsPausedUntil").ValueKind);
    }

    [Fact]
    public void A_pause_that_has_run_out_is_over()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var scanner = app.Services.GetRequiredService<ScannerService>();
        store.SetSetting("alertsPausedUntil", DateTime.UtcNow.AddMinutes(-1).ToString("o"));
        Assert.Null(scanner.PausedUntil);
        Assert.False(scanner.IsQuietNow());
    }

    [Fact]
    public async Task Alerts_that_come_up_during_a_pause_are_held_and_sent_when_it_ends()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var scanner = app.Services.GetRequiredService<ScannerService>();
        store.SetSetting("webhookUrl", "http://127.0.0.1:9/hook");      // nothing listens: the point is that nothing is attempted until the pause ends
        await scanner.PauseAlerts(60, default);
        await scanner.SendGenericAlert("The NAS is down", "It stopped answering.", "rule", default);
        await scanner.SendGenericAlert("The switch is down", "It stopped answering.", "rule", default);
        Assert.Equal(2, scanner.HeldCount);
        await scanner.PauseAlerts(0, default);
        Assert.Equal(0, scanner.HeldCount);
    }

    [Fact]
    public async Task Switched_off_it_refuses_a_pause_and_ends_one_that_was_running()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var scanner = app.Services.GetRequiredService<ScannerService>();
        await c.PostAsJsonAsync("/api/alerts/pause", new { minutes = 60 });
        Assert.True(scanner.IsQuietNow());

        var off = await c.PostAsJsonAsync("/api/settings/pause", new { enabled = false });
        Assert.True(off.IsSuccessStatusCode);
        Assert.False(scanner.IsQuietNow());
        Assert.False((await Hosts(c)).GetProperty("pauseEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/alerts/pause", new { minutes = 30 })).StatusCode);

        await c.PostAsJsonAsync("/api/settings/pause", new { enabled = true });
        Assert.True((await c.PostAsJsonAsync("/api/alerts/pause", new { minutes = 30 })).IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_pause_is_limited_to_three_days()
    {
        using var app = new BamfApp();
        var c = app.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/alerts/pause", new { minutes = ScannerService.MaxPauseMinutes + 1 })).StatusCode);
        Assert.True((await c.PostAsJsonAsync("/api/alerts/pause", new { minutes = ScannerService.MaxPauseMinutes })).IsSuccessStatusCode);
    }

    [Fact]
    public void Switching_it_off_in_settings_is_named_in_the_log_of_settings_changes()
    {
        Assert.Equal("The Pause alerts control", SettingsLog.Label("POST", "/api/settings/pause"));
    }
}
