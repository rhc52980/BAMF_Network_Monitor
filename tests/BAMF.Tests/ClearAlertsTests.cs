using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>The Alerts card's Clear: everything BAMF has kept and what the traffic monitor raised, gone, and the rest left as it was.</summary>
[Collection("Endpoints")]
public class ClearAlertsTests
{
    [Fact]
    public async Task Clear_empties_what_bamf_kept_and_what_the_traffic_monitor_raised_and_says_how_many()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var traffic = app.Services.GetRequiredService<TrafficMonitor>();
        store.AddAlert("port", "Telnet opened on nas", "Port 23 is open.");
        store.AddAlert("security", "A new scan", "x");
        store.AddAlert("cert", "A certificate expires", "y");
        traffic.NoteDhcpServer("192.168.30.1", null, "192.168.30.0/24");       // the first is learned quietly
        traffic.NoteDhcpServer("192.168.30.66", null, "192.168.30.0/24");       // a second is an alert
        Assert.Single(traffic.Alerts());

        var c = app.Client();
        var before = await c.GetFromJsonAsync<JsonElement>("/api/alerts");
        Assert.Equal(4, before.GetArrayLength());

        var r = await c.PostAsync("/api/alerts/clear", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(4, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cleared").GetInt32());

        Assert.Equal(0, (await c.GetFromJsonAsync<JsonElement>("/api/alerts")).GetArrayLength());
        Assert.Empty(store.GetAlerts());
        Assert.Empty(traffic.Alerts());

        // what the traffic monitor knows about stays known: the same server is not raised again at once
        traffic.NoteDhcpServer("192.168.30.66", null, "192.168.30.0/24");
        Assert.Empty(traffic.Alerts());
        Assert.Contains("192.168.30.66", traffic.KnownDhcp);

        // and clearing nothing is fine
        Assert.Equal(0, (await (await c.PostAsync("/api/alerts/clear", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cleared").GetInt32());
    }

    [Fact]
    public async Task Cleared_alerts_stop_counting_toward_the_score_and_a_new_one_after_shows_again()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var score = app.Services.GetRequiredService<HealthScore>();
        store.AddAlert("security", "Something", "x");
        Assert.Equal(1, score.Gather().SecurityAlerts);
        Assert.Equal(1, store.ClearAlerts());
        Assert.Equal(0, score.Gather().SecurityAlerts);
        store.AddAlert("security", "Something else", "y");
        Assert.Equal(1, score.Gather().SecurityAlerts);
        Assert.Single(store.GetAlerts());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_view_only_password_cannot_clear_them()
    {
        using var app = new BamfApp(settings: new Dictionary<string, string> { ["Bamf:Password"] = "main-pass", ["Bamf:ViewerPassword"] = "view-pass" });
        var store = app.Services.GetRequiredService<HostStore>();
        store.AddAlert("port", "Telnet opened on nas", "x");
        var c = app.Client();
        c.DefaultRequestHeaders.Authorization = BamfApp.Basic("view-pass");
        var denied = await c.PostAsync("/api/alerts/clear", null);
        Assert.True(denied.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, denied.StatusCode.ToString());
        Assert.Single(store.GetAlerts());

        c.DefaultRequestHeaders.Authorization = BamfApp.Basic("main-pass");
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/alerts/clear", null)).StatusCode);
        Assert.Empty(store.GetAlerts());
    }
}
