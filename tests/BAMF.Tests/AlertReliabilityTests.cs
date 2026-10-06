using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>Alerts that are tried again, gathered under the switch that took devices down, and calmed for a device that keeps dropping.</summary>
[Collection("Endpoints")]
public class AlertReliabilityTests
{
    private static readonly DateTime T = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    // ---------- retry ----------

    private sealed class Flaky(Func<int, HttpStatusCode> answer) : IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name) => new(new Handler(this));
        private sealed class Handler(Flaky owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(owner.answer(Interlocked.Increment(ref owner.Calls))));
        }
        private Func<int, HttpStatusCode> answer => answerField;
        private readonly Func<int, HttpStatusCode> answerField = answer;
    }

    private static (WebApplicationFactory<Program> App, ScannerService Scanner, Flaky Http) WithWebhook(BamfApp app, Func<int, HttpStatusCode> answer)
    {
        var http = new Flaky(answer);
        var host = app.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IHttpClientFactory>(http)));
        host.Services.GetRequiredService<HostStore>().SetSetting("webhookUrl", "https://hooks.example/api/webhooks/1/SECRET");
        return (host, host.Services.GetRequiredService<ScannerService>(), http);
    }

    [Fact]
    public async Task A_refused_alert_is_tried_again_a_minute_later_and_goes_out_when_the_destination_is_back()
    {
        using var app = new BamfApp();
        var (host, scanner, http) = WithWebhook(app, n => n == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent);
        using var _ = host;
        await scanner.SendGenericAlert("The NAS is down", "It stopped answering.", "rule", default);
        Assert.Equal(1, http.Calls);
        Assert.Equal(1, scanner.RetryQueued);

        Assert.Equal(0, await scanner.RetryFailedAlerts(default, DateTime.UtcNow.AddSeconds(20)));       // not yet due
        Assert.Equal(1, await scanner.RetryFailedAlerts(default, DateTime.UtcNow.AddMinutes(2)));
        Assert.Equal(2, http.Calls);
        Assert.Equal(0, scanner.RetryQueued);
    }

    [Fact]
    public async Task One_that_never_goes_through_is_tried_four_more_times_then_given_up_on_and_said_so()
    {
        using var app = new BamfApp();
        var (host, scanner, http) = WithWebhook(app, _ => HttpStatusCode.BadGateway);
        using var _ = host;
        var problems = host.Services.GetRequiredService<ProblemLog>();
        await scanner.SendGenericAlert("The NAS is down", "It stopped answering.", "rule", default);
        var t = DateTime.UtcNow;
        foreach (var wait in new[] { 2, 8, 25, 60 })
        {
            t = t.AddMinutes(wait);
            Assert.Equal(0, await scanner.RetryFailedAlerts(default, t));
        }
        Assert.Equal(5, http.Calls);                                // the first try and four more
        Assert.Equal(0, scanner.RetryQueued);
        Assert.Contains(problems.Rows(), r => r.Message.Contains("Gave up sending") && r.Message.Contains("The NAS is down"));
        Assert.DoesNotContain(problems.Rows(), r => r.Message.Contains("SECRET"));
    }

    [Fact]
    public async Task Switched_off_nothing_is_kept_to_retry()
    {
        using var app = new BamfApp();
        var (host, scanner, _) = WithWebhook(app, _ => HttpStatusCode.BadGateway);
        using var h = host;
        host.Services.GetRequiredService<HostStore>().SetSetting("alertRetry", "false");
        await scanner.SendGenericAlert("The NAS is down", "x", "rule", default);
        Assert.Equal(0, scanner.RetryQueued);
    }

    [Fact]
    public async Task A_retry_waits_while_alerts_are_paused_and_a_removed_destination_drops_its_alerts()
    {
        using var app = new BamfApp();
        var (host, scanner, http) = WithWebhook(app, n => n == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK);
        using var h = host;
        await scanner.SendGenericAlert("The NAS is down", "x", "rule", default);
        await scanner.PauseAlerts(60, default);
        Assert.Equal(0, await scanner.RetryFailedAlerts(default, DateTime.UtcNow.AddMinutes(2)));
        Assert.Equal(1, http.Calls);                                 // nothing sent while paused
        Assert.Equal(1, scanner.RetryQueued);
        await scanner.PauseAlerts(0, default);

        host.Services.GetRequiredService<HostStore>().SetSetting("webhookUrl", "");
        Assert.Equal(0, await scanner.RetryFailedAlerts(default, DateTime.UtcNow.AddMinutes(10)));
        Assert.Equal(0, scanner.RetryQueued);                        // its destination is gone, so it is forgotten
    }

    // ---------- grouping under a switch ----------

    private static HostRecord H(long id, bool online = false, string name = "") =>
        new(id, $"aa:00:00:00:00:{id:x2}", $"192.168.30.{id}", name == "" ? $"dev{id}" : name, "", "Acme", "192.168.30.0/24", online, true, false, true, false, "", "", "", "", "", "", "");

    private static SwitchRecord Sw(long id, string name, long hostId, long uplink = 0) =>
        new(id, name, 8, "192.168.30.0/24", hostId, uplink > 0 ? "switch" : "", uplink, 0, "switch", 0);

    private static Dictionary<long, HostRecord> All(params HostRecord[] hosts) => hosts.ToDictionary(h => h.Id);

    [Fact]
    public void Devices_behind_a_switch_that_went_down_are_said_once_under_it()
    {
        var sw = H(1, name: "rack-switch");
        var a = H(2); var b = H(3); var c = H(4); var other = H(9);
        var places = new Dictionary<long, (long, int)> { [2] = (10, 1), [3] = (10, 2), [4] = (10, 3) };
        var plan = StatusAlertPlanner.Make([sw, a, b, other], up: false, [Sw(10, "Rack switch", 1)], places, All(sw, a, b, c, other));
        var g = Assert.Single(plan.Groups);
        Assert.Equal(sw.Id, g.RootHost!.Id);
        Assert.Equal([2L, 3L], g.Behind.Select(h => h.Id).Order().ToArray());
        Assert.Equal(3, g.BehindTotal);                              // c is behind it and offline too, though not in this scan's list
        Assert.Equal([9L], plan.Single.Select(h => h.Id).ToArray());  // the one with no recorded place is said on its own
        var (title, detail) = StatusAlertPlanner.Describe(g, h => h.Hostname);
        Assert.Equal("rack-switch went offline, and 3 devices behind it", title);
        Assert.Contains("dev2", detail);
    }

    [Fact]
    public void A_lone_device_behind_a_dead_switch_is_said_on_its_own_unless_the_switch_alerts_too()
    {
        var a = H(2);
        var plan = StatusAlertPlanner.Make([a], up: false, [Sw(10, "Rack switch", 1)], new Dictionary<long, (long, int)> { [2] = (10, 1) }, All(H(1), a));
        Assert.Empty(plan.Groups);
        Assert.Single(plan.Single);

        var sw = H(1);
        var plan2 = StatusAlertPlanner.Make([sw, a], up: false, [Sw(10, "Rack switch", 1)], new Dictionary<long, (long, int)> { [2] = (10, 1) }, All(sw, a));
        Assert.Single(plan2.Groups);
        Assert.Empty(plan2.Single);
    }

    [Fact]
    public void Two_devices_behind_a_dead_switch_that_isnt_watched_are_said_together_without_it()
    {
        var sw = H(1);                                                // offline, but not among the changes (not watched)
        var a = H(2); var b = H(3);
        var plan = StatusAlertPlanner.Make([a, b], up: false, [Sw(10, "Rack switch", 1)], new Dictionary<long, (long, int)> { [2] = (10, 1), [3] = (10, 2) }, All(sw, a, b));
        var g = Assert.Single(plan.Groups);
        Assert.Null(g.RootHost);
        var (title, _) = StatusAlertPlanner.Describe(g, h => h.Hostname);
        Assert.Equal("2 watched devices went offline together", title);
    }

    [Fact]
    public void A_switch_that_is_still_up_gathers_nothing_and_a_switch_with_no_device_record_cannot_be_known_to_be_down()
    {
        var swUp = H(1, online: true);
        var a = H(2); var b = H(3);
        var places = new Dictionary<long, (long, int)> { [2] = (10, 1), [3] = (10, 2) };
        Assert.Empty(StatusAlertPlanner.Make([a, b], false, [Sw(10, "Rack switch", 1)], places, All(swUp, a, b)).Groups);
        Assert.Empty(StatusAlertPlanner.Make([a, b], false, [Sw(10, "Rack switch", 0)], places, All(a, b)).Groups);
    }

    [Fact]
    public void A_rack_switch_behind_a_dead_core_switch_is_said_under_the_core()
    {
        var core = H(1); var rack = H(2); var a = H(3); var b = H(4);
        var switches = new[] { Sw(10, "Core", 1), Sw(11, "Rack", 2, uplink: 10) };
        var places = new Dictionary<long, (long, int)> { [2] = (10, 1), [3] = (11, 1), [4] = (11, 2) };
        var plan = StatusAlertPlanner.Make([core, rack, a, b], false, switches, places, All(core, rack, a, b));
        var g = Assert.Single(plan.Groups);
        Assert.Equal(10, g.Root.Id);
        Assert.Equal(3, g.Behind.Count);
        Assert.Empty(plan.Single);
    }

    [Fact]
    public void Coming_back_is_gathered_the_same_way_and_says_so()
    {
        var sw = H(1, online: true); var a = H(2, online: true); var b = H(3, online: true);
        var places = new Dictionary<long, (long, int)> { [2] = (10, 1), [3] = (10, 2) };
        var plan = StatusAlertPlanner.Make([sw, a, b], up: true, [Sw(10, "Rack switch", 1)], places, All(sw, a, b));
        var g = Assert.Single(plan.Groups);
        Assert.True(g.Up);
        Assert.Contains("is back online, and so are 2 devices behind it", StatusAlertPlanner.Describe(g, h => h.Hostname).Title);
    }

    // ---------- flapping ----------

    private static long AddHost(HostStore store, int n)
    {
        var mac = $"ee:00:00:00:00:{n:x2}";
        store.UpsertSeen(mac, $"192.168.30.{200 + n}", "flappy" + n, "Acme", "192.168.30.0/24");
        var id = store.GetAll().Single(h => h.Mac == mac).Id;
        store.SetWatched(id, true);
        return id;
    }

    private static void Drops(HostStore store, long id, int n, DateTime from)
    {
        using var conn = new SqliteConnection($"Data Source={store.DatabasePath}");
        conn.Open();
        for (var i = 0; i < n; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO events (host_id, type, at) VALUES ($h, 'offline', $at)";
            cmd.Parameters.AddWithValue("$h", id);
            cmd.Parameters.AddWithValue("$at", from.AddMinutes(-5 * i).ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task A_device_that_keeps_dropping_gets_one_alert_saying_so_and_only_one_that_hour()
    {
        using var app = new BamfApp();
        var (host, scanner, http) = WithWebhook(app, _ => HttpStatusCode.OK);
        using var h = host;
        var store = host.Services.GetRequiredService<HostStore>();
        var id = AddHost(store, 1);
        Drops(store, id, 5, DateTime.UtcNow);
        var rec = store.GetAll().Single(x => x.Id == id);

        await scanner.SendStatusAlerts([rec], up: false, default);
        Assert.Equal(2, http.Calls);                                 // the flapping alert, and the usual offline alert
        await scanner.SendStatusAlerts([rec], up: false, default);
        Assert.Equal(3, http.Calls);                                 // only the usual one this time
    }

    [Fact]
    public async Task A_device_that_has_not_dropped_much_is_not_called_flapping()
    {
        using var app = new BamfApp();
        var (host, scanner, http) = WithWebhook(app, _ => HttpStatusCode.OK);
        using var h = host;
        var store = host.Services.GetRequiredService<HostStore>();
        var id = AddHost(store, 2);
        Drops(store, id, 2, DateTime.UtcNow);
        await scanner.SendStatusAlerts([store.GetAll().Single(x => x.Id == id)], up: false, default);
        Assert.Equal(1, http.Calls);
    }

    [Fact]
    public async Task Holding_a_flapping_devices_alerts_back_sends_only_the_flapping_one_and_follows_up_if_it_stays_down()
    {
        using var app = new BamfApp();
        var (host, scanner, http) = WithWebhook(app, _ => HttpStatusCode.OK);
        using var h = host;
        var store = host.Services.GetRequiredService<HostStore>();
        store.SetSetting("flapHold", "true");
        var id = AddHost(store, 3);
        Drops(store, id, 6, DateTime.UtcNow);
        var rec = store.GetAll().Single(x => x.Id == id);
        using (var conn = new SqliteConnection($"Data Source={store.DatabasePath}")) { conn.Open(); using var c = conn.CreateCommand(); c.CommandText = $"UPDATE hosts SET online = 0 WHERE id = {id}"; c.ExecuteNonQuery(); }

        await scanner.SendStatusAlerts([rec], up: false, default);
        Assert.Equal(1, http.Calls);                                 // just "keeps dropping"; its own offline alert is held
        Assert.True(scanner.IsHeldForFlapping(id));

        await scanner.FollowUpFlaps(default, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(1, http.Calls);                                 // too soon
        await scanner.FollowUpFlaps(default, DateTime.UtcNow.AddMinutes(16));
        Assert.Equal(2, http.Calls);                                 // still offline after 15 minutes: said after all
        Assert.False(scanner.IsHeldForFlapping(id));
    }

    [Fact]
    public async Task A_held_device_that_comes_back_is_not_followed_up()
    {
        using var app = new BamfApp();
        var (host, scanner, http) = WithWebhook(app, _ => HttpStatusCode.OK);
        using var h = host;
        var store = host.Services.GetRequiredService<HostStore>();
        store.SetSetting("flapHold", "true");
        var id = AddHost(store, 4);
        Drops(store, id, 6, DateTime.UtcNow);
        var rec = store.GetAll().Single(x => x.Id == id);
        await scanner.SendStatusAlerts([rec], up: false, default);
        await scanner.SendStatusAlerts([rec with { Online = true }], up: true, default);   // back: its back-online alert is held too
        Assert.Equal(1, http.Calls);
        await scanner.FollowUpFlaps(default, DateTime.UtcNow.AddHours(1));
        Assert.Equal(1, http.Calls);
    }

    // ---------- settings ----------

    [Fact]
    public async Task The_three_behaviours_can_be_switched_and_flapping_limited_and_none_is_secret()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var j = await c.GetFromJsonAsync<JsonElement>("/api/settings/alert-behaviour");
        Assert.True(j.GetProperty("retry").GetBoolean());
        Assert.True(j.GetProperty("cascade").GetBoolean());
        Assert.True(j.GetProperty("flapAlert").GetBoolean());
        Assert.False(j.GetProperty("flapHold").GetBoolean());
        Assert.Equal(4, j.GetProperty("flapDrops").GetInt32());

        var r = await c.PostAsJsonAsync("/api/settings/alert-behaviour", new { retry = false, cascade = false, flapHold = true, flapDrops = 6 });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        j = await c.GetFromJsonAsync<JsonElement>("/api/settings/alert-behaviour");
        Assert.False(j.GetProperty("retry").GetBoolean());
        Assert.False(j.GetProperty("cascade").GetBoolean());
        Assert.True(j.GetProperty("flapAlert").GetBoolean());        // not mentioned, so left as it was
        Assert.True(j.GetProperty("flapHold").GetBoolean());
        Assert.Equal(6, j.GetProperty("flapDrops").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/alert-behaviour", new { flapDrops = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/alert-behaviour", new { flapDrops = 21 })).StatusCode);
        Assert.Equal("Retrying alerts, grouping by switch, and flapping devices", SettingsLog.Label("POST", "/api/settings/alert-behaviour"));
    }
}
