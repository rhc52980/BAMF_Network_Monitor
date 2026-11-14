using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>The Pi-hole and AdGuard Home reader: who asked how much and what was blocked, a login that is closed again, and a password that is never shown.</summary>
[Collection("Endpoints")]
public class DnsFilterTests
{
    private sealed class Fake
    {
        public readonly List<string> Calls = new();
        public readonly List<string?> Sids = new();
        public readonly List<string?> Logins = new();
        public string? LoginBody;
        public int AuthStatus = 200;
        public int StatsStatus = 200;

        public Task<(int, string)> Answer(HttpRequestMessage req, CancellationToken _)
        {
            var path = req.RequestUri!.PathAndQuery;
            Calls.Add(req.Method + " " + path);
            Sids.Add(req.Headers.TryGetValues("sid", out var s) ? s.First() : null);
            Logins.Add(req.Headers.Authorization is { } a ? Encoding.UTF8.GetString(Convert.FromBase64String(a.Parameter!)) : null);
            if (path == "/api/auth" && req.Method == HttpMethod.Post)
            {
                LoginBody = req.Content!.ReadAsStringAsync().Result;
                return Task.FromResult((AuthStatus, AuthStatus == 200 ? """{"session":{"valid":true,"sid":"SID123","validity":300}}""" : "{}"));
            }
            if (path == "/api/auth") return Task.FromResult((204, ""));
            if (StatsStatus != 200) return Task.FromResult((StatsStatus, "{}"));
            if (path == "/api/stats/summary") return Task.FromResult((200, """{"queries":{"total":1000,"blocked":250,"percent_blocked":25.0}}"""));
            if (path.StartsWith("/api/stats/top_clients?blocked=true")) return Task.FromResult((200, """{"clients":[{"name":"tv","ip":"192.168.2.40","count":200}]}"""));
            if (path.StartsWith("/api/stats/top_clients")) return Task.FromResult((200, """{"clients":[{"name":"tv","ip":"192.168.2.40","count":600},{"name":"","ip":"192.168.2.41","count":400}]}"""));
            if (path == "/control/stats") return Task.FromResult((200, """{"num_dns_queries":2000,"num_blocked_filtering":500,"top_clients":[{"192.168.2.50":900},{"192.168.2.51":1100}]}"""));
            if (path.StartsWith("/control/querylog")) return Task.FromResult((200, """{"data":[{"client":"192.168.2.51"},{"client":"192.168.2.51"},{"client":"192.168.2.50"}]}"""));
            return Task.FromResult((404, ""));
        }
    }

    private static (DnsFilterReader R, HostStore Store, Fake F) Set(BamfApp app, string provider = "pihole", string secret = "app-pass", string user = "")
    {
        var r = app.Services.GetRequiredService<DnsFilterReader>();
        var store = app.Services.GetRequiredService<HostStore>();
        var f = new Fake();
        r.Send = f.Answer;
        store.SetSetting("dnsFilter", provider);
        store.SetSetting("dnsFilterUrl", "http://pi.test");
        store.SetSetting("dnsFilterUser", user);
        store.SetSetting("dnsFilterSecret", secret);
        return (r, store, f);
    }

    [Theory]
    [InlineData("http://192.168.2.5", "http://192.168.2.5")]
    [InlineData("http://192.168.2.5/admin/", "http://192.168.2.5")]
    [InlineData("  https://pi.hole:8443/  ", "https://pi.hole:8443")]
    public void The_address_is_tidied(string typed, string expected) => Assert.Equal(expected, DnsFilterReader.CleanUrl(typed).Url);

    [Theory]
    [InlineData("pi.hole")]
    [InlineData("ftp://pi.hole")]
    [InlineData("http://me:secret@pi.hole")]
    [InlineData("")]
    public void An_address_that_cannot_be_used_is_refused_and_a_login_in_it_is_not_kept(string typed) => Assert.NotNull(DnsFilterReader.CleanUrl(typed).Error);

    [Fact]
    public async Task A_pihole_is_logged_into_asked_three_things_and_logged_out_again()
    {
        using var app = new BamfApp();
        var (r, _, f) = Set(app);
        var snap = await r.Refresh(default);
        Assert.Null(snap!.Error);
        Assert.Equal("pihole", snap.Provider);
        Assert.Equal(1000, snap.Total);
        Assert.Equal(250, snap.Blocked);
        Assert.Equal(25, snap.Percent);
        Assert.Equal(2, snap.Clients.Count);
        Assert.Equal(("192.168.2.40", "tv", 600L, 200L), (snap.Clients[0].Ip, snap.Clients[0].Name, snap.Clients[0].Queries, snap.Clients[0].Blocked));
        Assert.Equal(0, snap.Clients[1].Blocked);                          // heard in the busiest list, not in the blocked one
        Assert.Contains("app-pass", f.LoginBody);
        Assert.Equal(new[] { "POST /api/auth", "GET /api/stats/summary", "GET /api/stats/top_clients?count=30", "GET /api/stats/top_clients?blocked=true&count=30", "DELETE /api/auth" }, f.Calls);
        Assert.All(f.Sids.Skip(1), sid => Assert.Equal("SID123", sid));    // every later call carries the session, the login seat is closed at the end
    }

    [Fact]
    public async Task A_pihole_with_no_password_is_asked_without_a_login()
    {
        using var app = new BamfApp();
        var (r, _, f) = Set(app, secret: "");
        Assert.Null((await r.Refresh(default))!.Error);
        Assert.DoesNotContain(f.Calls, c => c.Contains("/api/auth"));
    }

    [Fact]
    public async Task A_refused_password_or_an_older_pihole_says_so_and_what_was_read_before_stays()
    {
        using var app = new BamfApp();
        var (r, _, f) = Set(app);
        await r.Refresh(default);
        f.AuthStatus = 401;
        var bad = await r.Refresh(default);
        Assert.Contains("refused that password", bad!.Error);
        Assert.Equal(2, bad.Clients.Count);                                 // the last good read is still there to show
        Assert.Equal(1000, bad.Total);

        f.AuthStatus = 404;
        Assert.Contains("6 or newer", (await r.Refresh(default))!.Error);
        f.AuthStatus = 200; f.StatsStatus = 500;
        Assert.Contains("HTTP 500", (await r.Refresh(default))!.Error);
        Assert.Contains("DELETE /api/auth", f.Calls);                       // even a failed read gives the login back
    }

    [Fact]
    public async Task An_adguard_home_is_read_with_its_login_and_blocks_are_counted_from_its_log()
    {
        using var app = new BamfApp();
        var (r, _, f) = Set(app, "adguard", "pw", "admin");
        var snap = await r.Refresh(default);
        Assert.Null(snap!.Error);
        Assert.Equal("adguard", snap.Provider);
        Assert.Equal(2000, snap.Total);
        Assert.Equal(500, snap.Blocked);
        Assert.Equal(25, snap.Percent);
        Assert.Equal(("192.168.2.51", 1100L, 2L), (snap.Clients[0].Ip, snap.Clients[0].Queries, snap.Clients[0].Blocked));     // busiest first
        Assert.Equal(("192.168.2.50", 900L, 1L), (snap.Clients[1].Ip, snap.Clients[1].Queries, snap.Clients[1].Blocked));
        Assert.All(f.Logins, l => Assert.Equal("admin:pw", l));
        Assert.DoesNotContain(f.Calls, c => c.Contains("/api/auth"));
    }

    [Fact]
    public async Task Nothing_is_read_without_an_address()
    {
        using var app = new BamfApp();
        var (r, store, f) = Set(app);
        store.SetSetting("dnsFilterUrl", "");
        Assert.Null(await r.Refresh(default));
        Assert.Empty(f.Calls);
    }

    [Fact]
    public async Task The_api_never_gives_the_password_back_maps_clients_to_devices_and_forgets_everything()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var (r, store, f) = Set(app, secret: "");
        store.SetSetting("dnsFilterUrl", "");
        store.UpsertSeen("AA:00:00:00:00:40", "192.168.2.40", "living-room-tv", "Acme", "192.168.2.0/24");
        var hostId = store.GetAll().First(h => h.Ip == "192.168.2.40").Id;

        var off = await c.GetFromJsonAsync<JsonElement>("/api/dns-filter");
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/dns-filter", new { enabled = true })).StatusCode);   // no address yet
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/dns-filter", new { url = "ftp://x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/dns-filter", new { provider = "unbound" })).StatusCode);

        var saved = await c.PostAsJsonAsync("/api/settings/dns-filter", new { provider = "pihole", url = "http://pi.test/admin", secret = "TOPSECRET" });
        var text = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain("TOPSECRET", text);
        Assert.Contains("\"hasSecret\":true", text);
        Assert.Contains("\"url\":\"http://pi.test\"", text);
        Assert.Empty(f.Calls);                                              // saving reads nothing; switching on does

        var on = await (await c.PostAsJsonAsync("/api/settings/dns-filter", new { enabled = true })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(on.GetProperty("enabled").GetBoolean());
        var clients = on.GetProperty("last").GetProperty("clients");
        Assert.Equal("living-room-tv", clients[0].GetProperty("name").GetString());           // BAMF's name for it, not the filter's
        Assert.Equal(hostId, clients[0].GetProperty("hostId").GetInt64());
        Assert.Equal(JsonValueKind.Null, clients[1].GetProperty("hostId").ValueKind);        // a device BAMF hasn't seen
        Assert.DoesNotContain("TOPSECRET", await c.GetStringAsync("/api/dns-filter"));
        Assert.DoesNotContain("TOPSECRET", await c.GetStringAsync("/api/settings"));

        var forgot = await (await c.PostAsJsonAsync("/api/settings/dns-filter", new { url = "", secret = "", user = "", enabled = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(forgot.GetProperty("enabled").GetBoolean());
        Assert.False(forgot.GetProperty("hasSecret").GetBoolean());
        Assert.Equal("", forgot.GetProperty("url").GetString());
        Assert.Equal(JsonValueKind.Null, forgot.GetProperty("last").ValueKind);
    }

    [Fact]
    public async Task Reading_now_needs_an_address_works_with_the_switch_off_and_is_limited_to_one_every_ten_seconds()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var (_, store, _) = Set(app);
        store.SetSetting("dnsFilterUrl", "");
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync("/api/dns-filter/run", null)).StatusCode);
        await c.PostAsJsonAsync("/api/settings/dns-filter", new { url = "http://pi.test" });
        var go = await c.PostAsync("/api/dns-filter/run", null);
        Assert.True(go.IsSuccessStatusCode, await go.Content.ReadAsStringAsync());
        Assert.False((await go.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());
        Assert.Equal((HttpStatusCode)429, (await c.PostAsync("/api/dns-filter/run", null)).StatusCode);
    }
}
