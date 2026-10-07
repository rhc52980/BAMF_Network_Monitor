using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>
/// The DNS watch, the internet's quality (lost pings, jitter, DNS time) and a watched device slow to answer:
/// the rules on made-up readings, and the switches and answers in BAMF itself.
/// </summary>
[Collection("Endpoints")]
public class HealthWatchTests
{
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    // ---------- the DNS packets ----------

    [Fact]
    public void A_question_is_built_and_its_answer_read_back()
    {
        var q = DnsClient.Build(0x1234, "dns.google");
        Assert.Equal(new byte[] { 0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, q[..12]);
        Assert.Equal((byte)3, q[12]);                     // "dns"
        Assert.Equal((byte)6, q[16]);                     // "google"
        Assert.Equal(new byte[] { 0, 0, 1, 0, 1 }, q[^5..]);

        // A reply: the question echoed, then two A records whose names point back at it.
        var reply = new List<byte>(q) { };
        reply[2] = 0x81; reply[3] = 0x80;                 // response, recursion available, no error
        reply[7] = 2;                                     // two answers
        foreach (var a in new[] { new byte[] { 8, 8, 8, 8 }, new byte[] { 8, 8, 4, 4 } })
        {
            reply.AddRange(new byte[] { 0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0x0E, 0x10, 0, 4 });
            reply.AddRange(a);
        }
        var (addresses, rcode) = DnsClient.Parse(reply.ToArray());
        Assert.Equal(0, rcode);
        Assert.Equal(new[] { "8.8.8.8", "8.8.4.4" }, addresses.Select(x => x.ToString()));

        reply[3] = 0x83;                                  // no such name
        Assert.Equal(3, DnsClient.Parse(reply.ToArray()).RCode);
    }

    [Fact]
    public void A_canary_answered_with_a_strange_address_is_wrong()
    {
        Assert.False(DnsWatch.IsWrong("dns.google", new[] { Ip("8.8.8.8") }));
        Assert.False(DnsWatch.IsWrong("dns.google", new[] { Ip("8.8.4.4"), Ip("8.8.8.8") }));
        Assert.True(DnsWatch.IsWrong("dns.google", new[] { Ip("185.220.101.4") }));
        Assert.False(DnsWatch.IsWrong("dns.google", Array.Empty<IPAddress>()));          // no answer isn't a wrong one
        Assert.False(DnsWatch.IsWrong("example.org", new[] { Ip("1.2.3.4") }));           // not a canary
        Assert.EndsWith(".example.com", DnsWatch.NonsenseName());
        Assert.NotEqual(DnsWatch.NonsenseName(), DnsWatch.NonsenseName());
    }

    [Fact]
    public async Task A_lying_resolver_is_found_written_down_and_alerted()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var dns = app.Services.GetRequiredService<DnsWatch>();
        store.SetSetting("dnsWatch", "true");
        dns.Servers = () => new List<IPAddress> { Ip("192.168.1.1") };
        dns.Query = (server, name, _) => Task.FromResult(name switch
        {
            "dns.google" => new DnsClient.Answer(new List<IPAddress> { Ip("185.220.101.4") }, 12, 0, null),
            "one.one.one.one" => new DnsClient.Answer(new List<IPAddress> { Ip("1.1.1.1") }, 9, 0, null),
            "dns.quad9.net" => new DnsClient.Answer(new List<IPAddress> { Ip("9.9.9.9") }, 11, 0, null),
            _ => new DnsClient.Answer(new List<IPAddress> { Ip("10.10.10.10") }, 8, 0, null),    // makes up an answer for the nonsense name
        });
        var r = await dns.Check(CancellationToken.None);
        Assert.NotNull(r);
        Assert.Equal("192.168.1.1", r!.Server);
        var w = Assert.Single(r.WrongAnswers);
        Assert.Equal(("dns.google", "185.220.101.4", "8.8.8.8 or 8.8.4.4"), (w.Name, w.Got, w.Expected));
        Assert.True(r.InventsAnswers);
        Assert.Equal("10.10.10.10", r.Invented);
        Assert.Empty(r.NewServers);                        // the first set seen is just remembered

        var alerts = store.GetAlerts(20).Select(a => a.Title).ToList();
        Assert.Contains("Your DNS is giving wrong answers", alerts);
        Assert.Contains("Your DNS makes up answers for names that don't exist", alerts);
        Assert.Equal(r.CheckedAt, dns.Last!.CheckedAt);

        // The same again an hour later says nothing new; a new server does.
        dns.Servers = () => new List<IPAddress> { Ip("192.168.1.1"), Ip("45.33.32.156") };
        var again = await dns.Check(CancellationToken.None, DateTime.UtcNow.AddHours(1));
        Assert.Equal(new[] { "45.33.32.156" }, again!.NewServers);
        var titles = store.GetAlerts(20).Select(a => a.Title).ToList();
        Assert.Equal(1, titles.Count(t => t == "Your DNS is giving wrong answers"));
        var changed = store.GetAlerts(20).Single(a => a.Title == "Your DNS servers changed");
        Assert.Contains("an address on the internet, not your router", changed.Detail);

        var c = app.Client();
        var d = await c.GetFromJsonAsync<JsonElement>("/api/dns");
        Assert.True(d.GetProperty("enabled").GetBoolean());
        Assert.Equal("192.168.1.1", d.GetProperty("result").GetProperty("server").GetString());
        var sec = await c.GetFromJsonAsync<JsonElement>("/api/security");
        Assert.True(sec.GetProperty("dnsWatch").GetBoolean());
        Assert.Equal(1, sec.GetProperty("dns").GetProperty("wrongAnswers").GetArrayLength());
    }

    [Fact]
    public async Task An_honest_resolver_passes_quietly()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var dns = app.Services.GetRequiredService<DnsWatch>();
        store.SetSetting("dnsWatch", "true");
        dns.Servers = () => new List<IPAddress> { Ip("192.168.1.1") };
        dns.Query = (server, name, _) => Task.FromResult(DnsWatch.Canaries.TryGetValue(name, out var fixedIps)
            ? new DnsClient.Answer(fixedIps.Select(IPAddress.Parse).ToList(), 10, 0, null)
            : new DnsClient.Answer(new List<IPAddress>(), 10, 3, null));
        var r = await dns.Check(CancellationToken.None);
        Assert.Empty(r!.WrongAnswers);
        Assert.False(r.InventsAnswers);
        Assert.Null(r.Error);
        Assert.Empty(store.GetAlerts(20));
    }

    [Fact]
    public async Task The_dns_watch_switch_checks_straight_away_and_the_check_refuses_while_off()
    {
        using var app = new BamfApp();
        var dns = app.Services.GetRequiredService<DnsWatch>();
        dns.Servers = () => new List<IPAddress>();           // nothing to ask: the result says so
        var c = app.Client();
        var off = await c.PostAsync("/api/dns/check", null);
        Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
        var on = await c.PostAsJsonAsync("/api/settings/dns-watch", new { enabled = true });
        var d = await on.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(d.GetProperty("enabled").GetBoolean());
        Assert.Equal("This machine has no DNS server set.", d.GetProperty("result").GetProperty("error").GetString());
        var e = await c.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.True(e.GetProperty("editable").GetProperty("dnsWatch").GetBoolean());
    }

    // ---------- the line's quality ----------

    [Fact]
    public void Loss_jitter_and_dns_time_are_read_from_the_readings()
    {
        var r = WanQuality.Assess(new (int, int)[] { (20, 15), (-1, 15), (24, -1), (20, 30), (22, -2) });
        Assert.Equal(5, r.Samples);
        Assert.Equal(20.0, r.LossPercent);
        Assert.Equal(3, r.JitterMs);          // |24-20| + |20-24| + |22-20| over 3 steps = 10/3, rounded
        Assert.Equal(15, r.DnsMs);            // the median of 15, 15, 30
        Assert.Equal(4, r.DnsMeasured);
        Assert.Equal(1, r.DnsFailed);
        Assert.Empty(WanQuality.Problems(r)); // too few readings to call anything
    }

    [Fact]
    public void Problems_need_ten_readings_and_a_clear_line()
    {
        var steady = Enumerable.Repeat((20, 15), 12).ToList();
        Assert.Empty(WanQuality.Problems(WanQuality.Assess(steady)));

        var lossy = steady.Select((s, i) => i % 5 == 0 ? (-1, 15) : s).ToList();   // 3 of 12 lost: 25%
        Assert.Equal(new[] { "loss" }, WanQuality.Problems(WanQuality.Assess(lossy)));

        var jittery = Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? 20 : 140, 15)).ToList();
        Assert.Equal(new[] { "jitter" }, WanQuality.Problems(WanQuality.Assess(jittery)));

        var slowDns = Enumerable.Repeat((20, 450), 12).ToList();
        Assert.Equal(new[] { "dns" }, WanQuality.Problems(WanQuality.Assess(slowDns)));

        var failingDns = steady.Select((s, i) => i % 3 == 0 ? (20, -1) : s).ToList();    // 4 of 12 fail: 33%
        Assert.Equal(new[] { "dns" }, WanQuality.Problems(WanQuality.Assess(failingDns)));

        var oldRows = Enumerable.Repeat((20, -2), 12).ToList();                           // from before DNS was timed
        Assert.Empty(WanQuality.Problems(WanQuality.Assess(oldRows)));
    }

    [Fact]
    public async Task Failing_dns_over_a_quarter_hour_is_one_alert_and_one_when_it_is_over()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var wan = app.Services.GetRequiredService<WanWatch>();
        for (var i = 0; i < 12; i++) { store.AddWanSample(1, 20, -1); await Task.Delay(2); }
        await wan.CheckQuality(CancellationToken.None);
        Assert.Equal(new[] { "dns" }, wan.QualityProblems);
        var first = Assert.Single(store.GetAlerts(20));
        Assert.Equal("DNS lookups are slow", first.Title);
        Assert.Contains("12 of the last 12 DNS lookups", first.Detail);

        // Still failing: nothing more. Then a window of good lookups: over.
        await wan.CheckQuality(CancellationToken.None);
        Assert.Single(store.GetAlerts(20));
        await Task.Delay(20);
        var between = DateTime.UtcNow;
        await Task.Delay(20);
        for (var i = 0; i < 12; i++) { store.AddWanSample(1, 20, 14); await Task.Delay(2); }
        // The window is a quarter hour and the failures are seconds old, so judge it from a moment that leaves them out.
        await wan.CheckQuality(CancellationToken.None, between.Add(WanWatch.QualityWindow));
        Assert.Empty(wan.QualityProblems);
        Assert.Equal("DNS lookups are quick again", store.GetAlerts(20).First().Title);

        var c = app.Client();
        var d = await c.GetFromJsonAsync<JsonElement>("/api/wan");
        Assert.True(d.GetProperty("qualityAlert").GetBoolean());
        Assert.Equal(24, d.GetProperty("quality").GetProperty("samples").GetInt32());
        Assert.Equal(14, d.GetProperty("quality").GetProperty("dnsMs").GetInt32());
        Assert.Equal(14, d.GetProperty("samples").EnumerateArray().Last().GetProperty("dns").GetInt32());

        var off = await c.PostAsJsonAsync("/api/settings/wan-quality", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False(wan.QualityEnabled);
    }

    [Fact]
    public async Task Nothing_is_said_about_quality_while_the_switch_is_off()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var wan = app.Services.GetRequiredService<WanWatch>();
        store.SetSetting("wanQuality", "false");
        for (var i = 0; i < 12; i++) { store.AddWanSample(1, -1, -1); await Task.Delay(2); }
        await wan.CheckQuality(CancellationToken.None);
        Assert.Empty(wan.QualityProblems);
        Assert.Empty(store.GetAlerts(20));
    }

    // ---------- a watched device slow to answer ----------

    [Fact]
    public void Five_slow_pings_open_an_episode_and_three_quick_ones_close_it()
    {
        var e = new LatencyEpisodes();
        for (var i = 0; i < 4; i++) Assert.Null(e.Observe(1, 300, 250));
        Assert.Equal("high", e.Observe(1, 320, 250));
        Assert.True(e.IsHigh(1));
        Assert.Null(e.Observe(1, 400, 250));             // still slow: nothing more
        Assert.Equal(400, e.Worst(1));
        Assert.Null(e.Observe(1, null, 250));             // no reply says nothing either way
        Assert.Null(e.Observe(1, 20, 250));
        Assert.Null(e.Observe(1, 300, 250));              // a slow one in between starts the count over
        Assert.Null(e.Observe(1, 20, 250));
        Assert.Null(e.Observe(1, 20, 250));
        Assert.Equal("back", e.Observe(1, 20, 250));
        Assert.False(e.IsHigh(1));
    }

    [Fact]
    public void A_quick_ping_in_the_run_means_it_is_not_slow_yet()
    {
        var e = new LatencyEpisodes();
        for (var i = 0; i < 4; i++) Assert.Null(e.Observe(7, 300, 250));
        Assert.Null(e.Observe(7, 100, 250));
        for (var i = 0; i < 4; i++) Assert.Null(e.Observe(7, 300, 250));
        Assert.Equal("high", e.Observe(7, 300, 250));
        e.Forget(7);
        Assert.False(e.IsHigh(7));
    }

    [Fact]
    public async Task The_slow_device_switch_and_its_limit_round_trip()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var bad = await c.PostAsJsonAsync("/api/settings/latency-alert", new { ms = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var ok = await c.PostAsJsonAsync("/api/settings/latency-alert", new { enabled = false, ms = 400 });
        var d = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(d.GetProperty("enabled").GetBoolean());
        Assert.Equal(400, d.GetProperty("ms").GetInt32());
        var e = await c.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.False(e.GetProperty("editable").GetProperty("latencyAlert").GetBoolean());
        Assert.Equal(400, e.GetProperty("editable").GetProperty("latencyAlertMs").GetInt32());
        var scanner = app.Services.GetRequiredService<ScannerService>();
        Assert.Equal(400, scanner.LatencyAlertMs);
        Assert.False(scanner.LatencyAlertEnabled);
    }
}
