using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>
/// The service watch: its card made from readings, the alerts on made-up probes, adding and removing through the API, and the score.
/// </summary>
[Collection("Endpoints")]
public class ServiceWatchTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private static HostStore.WatchedService Svc(int slow = 0) => new(1, 7, "Plex", "port", 32400, "/", false, slow, Now.ToString("o"));
    private static HostStore.ServiceCheck Chk(double minutesAgo, bool ok = true, int? ms = 10, bool off = false) =>
        new(Now.AddMinutes(-minutesAgo).ToString("o"), ok, ok ? ms : null, ok ? null : "no answer", off);

    // ---------- the card, from readings ----------

    [Fact]
    public void A_day_of_readings_becomes_a_strip_an_uptime_and_a_state()
    {
        var checks = new List<HostStore.ServiceCheck>();
        for (var m = 1400; m >= 1; m -= 5) checks.Add(Chk(m));
        var row = ServiceWatch.Summarize(Svc(), "media-server", "192.168.30.40", false, checks, Now, false);
        Assert.Equal(48, row.Strip.Length);
        Assert.Equal("up", row.State);
        Assert.Equal(100.0, row.Uptime);
        Assert.Equal(10, row.Ms);
        Assert.Null(row.DownSince);
        Assert.Null(row.LastDown);
        Assert.All(row.Strip, c => Assert.True(c is 'u' or 'n'));
        Assert.Equal('u', row.Strip[^1]);
    }

    [Fact]
    public void Two_failures_in_a_row_are_down_and_one_is_not()
    {
        var one = new List<HostStore.ServiceCheck> { Chk(3), Chk(2), Chk(1, ok: false) };
        Assert.Equal("up", ServiceWatch.Summarize(Svc(), "d", "1.2.3.4", false, one, Now, false).State);          // a blip, not an outage

        var two = new List<HostStore.ServiceCheck> { Chk(4), Chk(3, ok: false), Chk(2, ok: false), Chk(1, ok: false) };
        var row = ServiceWatch.Summarize(Svc(), "d", "1.2.3.4", false, two, Now, false);
        Assert.Equal("down", row.State);
        Assert.Equal(two[1].At, row.DownSince);
        Assert.Equal("no answer", row.Error);
        Assert.Null(row.Ms);
        Assert.Equal(25.0, row.Uptime);                                                                           // one of four passed
    }

    [Fact]
    public void An_earlier_outage_is_remembered_with_its_length_and_the_strip_goes_red_for_it()
    {
        var checks = new List<HostStore.ServiceCheck>();
        for (var m = 600; m >= 1; m--) checks.Add(Chk(m, ok: m is not (500 or 499 or 498 or 497)));       // four minutes down, ten hours ago
        var row = ServiceWatch.Summarize(Svc(), "d", "1.2.3.4", false, checks, Now, false);
        Assert.Equal("up", row.State);
        Assert.Equal(4, row.LastDown!.Minutes);
        Assert.Equal(checks[^500].At, row.LastDown.At);
        Assert.Contains('d', row.Strip);
        Assert.Equal(1, row.Strip.Count(c => c == 'd'));
    }

    [Fact]
    public void A_device_that_is_off_is_not_counted_against_its_service()
    {
        var checks = new List<HostStore.ServiceCheck> { Chk(5), Chk(4, ok: false, off: true), Chk(3, ok: false, off: true), Chk(2, ok: false, off: true) };
        var row = ServiceWatch.Summarize(Svc(), "d", "1.2.3.4", true, checks, Now, false);
        Assert.Equal("off", row.State);
        Assert.Null(row.DownSince);
        Assert.Equal(100.0, row.Uptime);                                                                          // only the real reading counts
        Assert.Equal("waiting", ServiceWatch.Summarize(Svc(), "d", "1.2.3.4", false, new List<HostStore.ServiceCheck>(), Now, false).State);
    }

    [Fact]
    public void A_slow_service_shows_slow_and_amber_bars()
    {
        var checks = new List<HostStore.ServiceCheck>();
        for (var m = 60; m >= 1; m--) checks.Add(Chk(m, ms: m < 20 ? 1500 : 20));
        var row = ServiceWatch.Summarize(Svc(slow: 1000), "d", "1.2.3.4", false, checks, Now, true);
        Assert.Equal("slow", row.State);
        Assert.Contains('s', row.Strip);
        Assert.Equal("slow", ServiceWatch.Summarize(Svc(slow: 1000), "d", "1.2.3.4", false, checks, Now, true).State);
        Assert.Equal("up", ServiceWatch.Summarize(Svc(slow: 1000), "d", "1.2.3.4", false, checks, Now, false).State);
    }

    [Fact]
    public void Names_come_from_what_the_port_usually_is()
    {
        Assert.Equal("Web page", ServiceWatch.NameFor(80, "web"));
        Assert.Equal("Home Assistant", ServiceWatch.NameFor(8123, "web"));
        Assert.Equal("SSH", ServiceWatch.NameFor(22, "port"));
        Assert.Equal("Port 4242", ServiceWatch.NameFor(4242, "port"));
        Assert.Equal("1.4 s", ServiceWatch.FormatMs(1400));
        Assert.Equal("14 ms", ServiceWatch.FormatMs(14));
    }

    [Fact]
    public async Task A_port_check_sees_an_open_port_and_a_closed_one()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        var open = await ServiceWatch.PortOnce("127.0.0.1", port, CancellationToken.None);
        Assert.True(open.Ok);
        Assert.NotNull(open.Ms);
        l.Stop();
        var closed = await ServiceWatch.PortOnce("127.0.0.1", port, CancellationToken.None);
        Assert.False(closed.Ok);
        Assert.Contains("listening", closed.Error);
    }

    // ---------- running them and saying so ----------

    private static long AddHost(HostStore store, int n, bool watched = false)
    {
        var mac = $"ee:00:00:00:10:{n:x2}";
        store.UpsertSeen(mac, $"192.168.30.{120 + n}", "media" + n, "Acme", "192.168.30.0/24");
        var id = store.GetAll().Single(h => h.Mac == mac).Id;
        if (watched) store.SetWatched(id, true);
        return id;
    }

    private static void SetOnline(HostStore store, long id, bool on)
    {
        using var conn = new SqliteConnection($"Data Source={store.DatabasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE hosts SET online = $o WHERE id = $i";
        cmd.Parameters.AddWithValue("$o", on ? 1 : 0);
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
    }

    private static List<string> Mail(ScannerService scanner)
    {
        var sent = new List<string>();
        scanner.EmailSender = (_, subject, _, _) => { sent.Add(subject); return Task.CompletedTask; };
        scanner.SaveExtraDestinations(new[] { new ScannerService.DestinationInput(null, "Mail", EmailTarget.Build("smtp.example.com", 587, true, "u", "p", "bamf@example.com", new[] { "me@example.com" }), "email", new() { "status" }) });
        return sent;
    }

    [Fact]
    public async Task A_service_that_fails_twice_alerts_once_and_again_when_it_is_back()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var watch = app.Services.GetRequiredService<ServiceWatch>();
        var sent = Mail(app.Services.GetRequiredService<ScannerService>());
        var id = AddHost(store, 1);
        var svc = store.AddService(id, "Plex", "port", 32400, "/", false, 0)!;
        var answer = new Queue<ServiceWatch.Sample>(new[]
        {
            new ServiceWatch.Sample(true, 5, null), new ServiceWatch.Sample(false, null, "Nothing is listening on port 32400."),
            new ServiceWatch.Sample(false, null, "Nothing is listening on port 32400."), new ServiceWatch.Sample(false, null, "Nothing is listening on port 32400."),
            new ServiceWatch.Sample(true, 6, null), new ServiceWatch.Sample(true, 6, null),
        });
        watch.Probe = (_, _, _) => Task.FromResult(answer.Dequeue());

        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(1, await watch.RunOnce(CancellationToken.None, Now.AddMinutes(i)));
            if (i == 1) Assert.Empty(sent);                                   // one failure says nothing
            if (i == 2) Assert.Equal("[BAMF] Plex on media1 isn't answering", Assert.Single(sent));
            if (i == 3) Assert.Single(sent);                                  // still down: not again
        }
        Assert.Equal(2, sent.Count);
        Assert.Equal("[BAMF] Plex on media1 is answering again", sent[1]);
        Assert.Equal(6, store.GetServiceChecks(svc.Id, Now.AddHours(-1)).Count);
    }

    [Fact]
    public async Task A_device_that_is_off_a_snoozed_one_and_a_forgotten_one_are_not_asked_or_not_alerted()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var watch = app.Services.GetRequiredService<ServiceWatch>();
        var sent = Mail(app.Services.GetRequiredService<ScannerService>());
        var off = AddHost(store, 2);
        store.AddService(off, "Plex", "port", 32400, "/", false, 0);
        SetOnline(store, off, false);
        var asked = 0;
        watch.Probe = (_, _, _) => { asked++; return Task.FromResult(new ServiceWatch.Sample(false, null, "no answer")); };

        for (var i = 0; i < 4; i++) await watch.RunOnce(CancellationToken.None, Now.AddMinutes(i));
        Assert.Empty(sent);                                                   // the device's own alert says it
        Assert.Equal(4, asked);
        Assert.All(store.GetServiceChecks(1, Now.AddHours(-1)), c => Assert.True(c.DeviceOff));

        var gone = AddHost(store, 3);
        store.AddService(gone, "SSH", "port", 22, "/", false, 0);
        store.SetForgotten(gone, true);
        asked = 0;
        await watch.RunOnce(CancellationToken.None, Now.AddMinutes(10));
        Assert.Equal(1, asked);                                               // only the one that isn't forgotten
    }

    [Fact]
    public async Task A_slow_service_alerts_after_five_slow_answers_and_again_when_normal()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var watch = app.Services.GetRequiredService<ServiceWatch>();
        var sent = Mail(app.Services.GetRequiredService<ScannerService>());
        var id = AddHost(store, 4);
        store.AddService(id, "NAS sign-in", "web", 5001, "/", true, 1000);
        var ms = 20;
        watch.Probe = (_, _, _) => Task.FromResult(new ServiceWatch.Sample(true, ms, null));

        ms = 1500;
        for (var i = 0; i < 4; i++) await watch.RunOnce(CancellationToken.None, Now.AddMinutes(i));
        Assert.Empty(sent);
        await watch.RunOnce(CancellationToken.None, Now.AddMinutes(4));
        Assert.Equal("[BAMF] NAS sign-in on media4 is slow to answer: 1.5 s", Assert.Single(sent));
        Assert.Equal("slow", watch.Snapshot(Now.AddMinutes(5)).Single().State);

        ms = 20;
        for (var i = 5; i < 8; i++) await watch.RunOnce(CancellationToken.None, Now.AddMinutes(i));
        Assert.Equal(2, sent.Count);
        Assert.Equal("[BAMF] NAS sign-in on media4 is answering normally again: 20 ms", sent[1]);
    }

    [Fact]
    public async Task Readings_older_than_three_days_are_dropped_and_a_removed_service_takes_its_readings_with_it()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var id = AddHost(store, 5);
        var svc = store.AddService(id, "Plex", "port", 32400, "/", false, 0)!;
        store.AddServiceCheck(svc.Id, DateTime.UtcNow.AddDays(-4), true, 5, null, false);
        store.AddServiceCheck(svc.Id, DateTime.UtcNow.AddMinutes(-1), true, 5, null, false);
        store.PruneServiceChecks(DateTime.UtcNow.AddDays(-ServiceWatch.KeepDays));
        Assert.Single(store.GetServiceChecks(svc.Id, DateTime.UtcNow.AddDays(-10)));
        Assert.True(store.DeleteService(svc.Id));
        Assert.Empty(store.GetServiceChecks(svc.Id, DateTime.UtcNow.AddDays(-10)));
        Assert.False(store.DeleteService(svc.Id));
        await Task.CompletedTask;
    }

    // ---------- the API ----------

    [Fact]
    public async Task Services_are_added_checked_listed_and_removed_through_the_api()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var watch = app.Services.GetRequiredService<ServiceWatch>();
        var id = AddHost(store, 6);
        watch.Probe = (s, ip, _) => Task.FromResult(new ServiceWatch.Sample(true, 120, null));
        var c = app.Client();

        var empty = await c.GetFromJsonAsync<JsonElement>("/api/service-watch");
        Assert.True(empty.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, empty.GetProperty("services").GetArrayLength());
        Assert.Equal("media6", empty.GetProperty("devices")[0].GetProperty("name").GetString());

        // Tried first, not saved.
        var tried = await (await c.PostAsJsonAsync("/api/service-watch/try", new { hostId = id, kind = "web", port = 8123, path = "/", slowMs = 100 })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(tried.GetProperty("ok").GetBoolean());
        Assert.True(tried.GetProperty("slow").GetBoolean());
        Assert.Equal("http://192.168.30.126:8123/", tried.GetProperty("url").GetString());
        Assert.Empty(store.GetServices());

        // What it won't take.
        foreach (var bad in new object[]
        {
            new { hostId = 9999, kind = "port", port = 22 },
            new { hostId = id, kind = "ping", port = 22 },
            new { hostId = id, kind = "port", port = 0 },
            new { hostId = id, kind = "port", port = 70000 },
            new { hostId = id, kind = "web", port = 80, path = "/a b" },
            new { hostId = id, kind = "port", port = 22, slowMs = 5 },
            new { hostId = id, kind = "port", port = 22, name = new string('x', 61) },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/service-watch", bad)).StatusCode);

        var add = await c.PostAsJsonAsync("/api/service-watch", new { hostId = id, kind = "web", port = 8123, path = "admin", https = false, slowMs = 800 });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var svc = Assert.Single(store.GetServices());
        Assert.Equal(("Home Assistant", "web", 8123, "/admin", 800), (svc.Name, svc.Kind, svc.Port, svc.Path, svc.SlowMs));
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/service-watch", new { hostId = id, kind = "web", port = 8123, path = "/admin" })).StatusCode);

        var list = await c.GetFromJsonAsync<JsonElement>("/api/service-watch");
        var row = list.GetProperty("services")[0];
        Assert.Equal("waiting", row.GetProperty("state").GetString());
        Assert.Equal(48, row.GetProperty("strip").GetString()!.Length);
        Assert.Equal("media6", row.GetProperty("device").GetString());

        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"/api/service-watch/{svc.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/service-watch/{svc.Id}")).StatusCode);
    }

    [Fact]
    public async Task The_switch_and_the_limit_on_how_many_are_watched_work_and_the_setting_travels()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var id = AddHost(store, 7);
        var c = app.Client();
        for (var p = 1; p <= ServiceWatch.Max; p++) store.AddService(id, "Port " + p, "port", p, "/", false, 0);
        var full = await c.PostAsJsonAsync("/api/service-watch", new { hostId = id, kind = "port", port = 4000 });
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.Empty((await c.GetFromJsonAsync<JsonElement>("/api/service-watch")).GetProperty("suggestions").EnumerateArray());

        var off = await (await c.PostAsJsonAsync("/api/settings/service-watch", new { enabled = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/service-watch")).GetProperty("enabled").GetBoolean());
        Assert.Contains("serviceWatch", HostStore.TransferKeys);
    }

    [Fact]
    public void Open_ports_BAMF_found_become_suggestions_until_they_are_watched()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var watch = app.Services.GetRequiredService<ServiceWatch>();
        var id = AddHost(store, 8);
        store.RecordPortScan(id, new[] { 22, 8123, 23, 5001, 9999 }, new[] { (22, "SSH"), (8123, "Home Assistant"), (23, "Telnet"), (5001, "Synology"), (9999, "x") });
        var s = watch.Suggestions();
        Assert.Equal(new[] { 22, 5001, 8123 }, s.Select(x => x.Port).OrderBy(x => x));       // Telnet and the odd port aren't offered
        Assert.True(s.Single(x => x.Port == 5001).Https);
        Assert.Equal("port", s.Single(x => x.Port == 22).Kind);
        Assert.Equal("Home Assistant", s.Single(x => x.Port == 8123).Name);

        store.AddService(id, "Home Assistant", "web", 8123, "/", false, 0);
        Assert.DoesNotContain(watch.Suggestions(), x => x.Port == 8123);
    }

    // ---------- the score ----------

    [Fact]
    public void A_service_that_is_down_takes_points_off_the_score()
    {
        var clean = new HealthScore.Inputs(0, 0, 0, 0, 0, 0, 0, false, false, 0, 0, 0, 0, false, false, false);
        Assert.Equal(100, HealthScore.Compute(clean).Value);
        var one = HealthScore.Compute(clean with { ServicesDown = 1 });
        Assert.Equal(96, one.Value);
        Assert.Equal("A watched service is down", one.Reasons.Single().Text);
        var many = HealthScore.Compute(clean with { ServicesDown = 9 });
        Assert.Equal(88, many.Value);                                                         // capped at twelve
        Assert.Equal("9 watched services are down", many.Reasons.Single().Text);
    }
}
