using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BAMF.Tests;

/// <summary>
/// BAMF's API as a browser, a script or another BAMF sees it: each test starts
/// BAMF on a new database and calls it. Test passwords only.
/// </summary>
[Collection("Endpoints")]
public class EndpointTests
{
    private const string Main = "main-test-pass-1";
    private const string Viewer = "viewer-test-pass-1";

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private static async Task<HttpResponseMessage> Post(HttpClient c, string url, object body) => await c.PostAsJsonAsync(url, body);

    private static async Task SetMain(HttpClient c) =>
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/api/settings/password", new { role = "admin", password = Main })).StatusCode);

    private static HttpRequestMessage Get(string url, string? password = null, string? fetchMode = null)
    {
        var m = new HttpRequestMessage(HttpMethod.Get, url);
        if (password is not null) m.Headers.Authorization = BamfApp.Basic(password);
        if (fetchMode is not null) m.Headers.Add("Sec-Fetch-Mode", fetchMode);
        return m;
    }

    // ---------- open, and setup ----------

    [Fact]
    public async Task A_new_install_is_open_and_offers_setup()
    {
        using var app = new BamfApp();
        var c = app.Client();
        Assert.Equal(JsonValueKind.Array, (await c.GetFromJsonAsync<JsonElement>("/api/hosts")).GetProperty("hosts").ValueKind);
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/auth")).GetProperty("required").GetBoolean());
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/setup")).GetProperty("pending").GetBoolean());
    }

    [Fact]
    public async Task Setup_is_done_once()
    {
        using var app = new BamfApp();
        var c = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/api/setup", new { skip = true })).StatusCode);
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/setup")).GetProperty("pending").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await Post(c, "/api/setup", new { skip = true })).StatusCode);
    }

    [Fact]
    public async Task Only_private_networks_can_be_added_from_the_dashboard()
    {
        using var app = new BamfApp();
        var c = app.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, "/api/settings/networks", new { networks = new[] { "8.8.8.0/24" } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/api/settings/networks", new { networks = new[] { "192.168.40.0/24" } })).StatusCode);
        var n = (await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("editable").GetProperty("networks");
        Assert.Equal("settings", n.GetProperty("source").GetString());
        Assert.Equal("192.168.40.0/24", n.GetProperty("list")[0].GetString());
    }

    // ---------- signing in ----------

    [Fact]
    public async Task With_a_password_each_kind_of_caller_is_asked_its_own_way()
    {
        using var app = new BamfApp();
        var c = app.Client();
        await SetMain(c);
        var fresh = app.Client();   // no cookie

        // A browser opening the page goes to the sign-in page.
        var nav = await fresh.SendAsync(Get("/", fetchMode: "navigate"));
        Assert.Equal(HttpStatusCode.Redirect, nav.StatusCode);
        Assert.StartsWith("/signin", nav.Headers.Location!.OriginalString);
        // The page's own requests are told to sign in, without a Basic prompt.
        var fetch = await fresh.SendAsync(Get("/api/hosts", fetchMode: "cors"));
        Assert.Equal(HttpStatusCode.Unauthorized, fetch.StatusCode);
        Assert.True(fetch.Headers.Contains("X-BAMF-SignIn"));
        // A script is asked for Basic auth, and let in with it.
        var script = await fresh.SendAsync(Get("/api/hosts"));
        Assert.Equal(HttpStatusCode.Unauthorized, script.StatusCode);
        Assert.Contains("Basic", script.Headers.WwwAuthenticate.ToString());
        Assert.Equal(HttpStatusCode.OK, (await fresh.SendAsync(Get("/api/hosts", Main))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.SendAsync(Get("/api/hosts", "wrong-password"))).StatusCode);
        // What the sign-in page needs stays open.
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await fresh.SendAsync(Get("/manifest.webmanifest"))).StatusCode);
        // So do the service worker and its offline page, which a phone fetches without the cookie.
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await fresh.SendAsync(Get("/sw.js"))).StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await fresh.SendAsync(Get("/offline.html"))).StatusCode);
    }

    [Fact]
    public async Task Signing_in_gives_a_browser_a_session()
    {
        using var app = new BamfApp();
        await SetMain(app.Client());
        var browser = app.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(browser, "/api/signin", new { password = "wrong-password" })).StatusCode);
        var ok = await Post(browser, "/api/signin", new { password = Main });
        Assert.Equal("admin", (await Json(ok)).GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(Get("/api/hosts", fetchMode: "cors"))).StatusCode);
        await Post(browser, "/api/signout", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(Get("/api/hosts", fetchMode: "cors"))).StatusCode);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_address_out()
    {
        using var app = new BamfApp();
        await SetMain(app.Client());
        var c = app.Client();
        for (var i = 0; i < 5; i++) await Post(c, "/api/signin", new { password = "wrong-password" });
        // Now even the right one is turned away until the lockout ends.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post(c, "/api/signin", new { password = Main })).StatusCode);
    }

    [Fact]
    public async Task The_view_only_password_can_look_but_not_change_or_take_a_backup()
    {
        using var app = new BamfApp();
        var c = app.Client();
        await SetMain(c);
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/api/settings/password", new { role = "viewer", current = Main, password = Viewer })).StatusCode);

        var v = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await v.SendAsync(Get("/api/hosts", Viewer))).StatusCode);
        var write = new HttpRequestMessage(HttpMethod.Post, "/api/settings/newdays") { Content = JsonContent.Create(new { days = 3 }) };
        write.Headers.Authorization = BamfApp.Basic(Viewer);
        var refused = await v.SendAsync(write);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.True(refused.Headers.Contains("X-BAMF-ViewOnly"));
        Assert.Equal(HttpStatusCode.Forbidden, (await v.SendAsync(Get("/api/backup", Viewer))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await v.SendAsync(Get("/api/backup", Main))).StatusCode);
    }

    [Fact]
    public async Task A_password_is_only_changed_with_the_current_one()
    {
        using var app = new BamfApp();
        var c = app.Client();
        await SetMain(c);
        var other = app.Client();
        var wrong = new HttpRequestMessage(HttpMethod.Post, "/api/settings/password") { Content = JsonContent.Create(new { role = "admin", current = "not-it-at-all", password = "another-pass-1" }) };
        wrong.Headers.Authorization = BamfApp.Basic(Main);
        Assert.Equal(HttpStatusCode.BadRequest, (await other.SendAsync(wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.SendAsync(Get("/api/hosts", Main))).StatusCode);
    }

    [Fact]
    public async Task A_forgotten_password_is_reset_by_a_file_beside_the_database_and_nothing_else_is_lost()
    {
        using var first = new BamfApp();
        var c = first.Client();
        await SetMain(c);
        var named = await Post(c, "/api/settings/newdays", new { days = 3 });   // any setting, to see it kept
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.Client().SendAsync(Get("/api/hosts"))).StatusCode);

        // The same database, with a reset-password file beside it, at the next start.
        File.WriteAllText(Path.Combine(first.Dir, "reset-password"), "");
        using var second = new BamfApp(first.Dir);
        var d = second.Client();
        Assert.False((await d.GetFromJsonAsync<JsonElement>("/api/auth")).GetProperty("required").GetBoolean());
        Assert.False(File.Exists(Path.Combine(first.Dir, "reset-password")));
        Assert.Equal(3, (await d.GetFromJsonAsync<JsonElement>("/api/hosts")).GetProperty("newDays").GetInt32());
    }

    // ---------- saved passwords for other servers ----------

    [Fact]
    public async Task A_saved_mqtt_password_is_never_sent_back_and_stays_with_its_broker()
    {
        using var app = new BamfApp();
        var c = app.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, "/api/settings/mqtt", new { server = "not a host/x", port = 1883 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, "/api/settings/mqtt", new { server = "10.0.0.2", port = 70000 })).StatusCode);

        await Post(c, "/api/settings/mqtt", new { server = "10.0.0.2", port = 1883, username = "u", password = "broker-secret-1" });
        var body = await c.GetStringAsync("/api/settings");
        Assert.DoesNotContain("broker-secret-1", body);
        var m = JsonDocument.Parse(body).RootElement.GetProperty("editable").GetProperty("mqtt");
        Assert.True(m.GetProperty("password").GetBoolean());

        // Kept for the same broker, dropped for another.
        await Post(c, "/api/settings/mqtt", new { server = "10.0.0.2", port = 1883, username = "u", password = (string?)null, discovery = false });
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("editable").GetProperty("mqtt").GetProperty("password").GetBoolean());
        await Post(c, "/api/settings/mqtt", new { server = "10.9.9.9", port = 1883, username = "u", password = (string?)null });
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("editable").GetProperty("mqtt").GetProperty("password").GetBoolean());
    }

    [Fact]
    public async Task A_saved_remote_password_stays_only_while_its_address_does()
    {
        using var app = new BamfApp();
        var c = app.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, "/api/settings/remotes", new { remotes = new[] { new { name = "Cabin", url = "ftp://x" } } })).StatusCode);
        await Post(c, "/api/settings/remotes", new { remotes = new[] { new { name = "Cabin", url = "http://10.1.0.5:8840", password = "remote-secret-1" } } });
        Assert.DoesNotContain("remote-secret-1", await c.GetStringAsync("/api/settings"));
        await Post(c, "/api/settings/remotes", new { remotes = new[] { new { name = "Cabin", url = "http://10.1.0.5:8840", password = (string?)null } } });
        Assert.True(Remote(await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("password").GetBoolean());
        await Post(c, "/api/settings/remotes", new { remotes = new[] { new { name = "Cabin", url = "http://10.9.9.9:8840", password = (string?)null } } });
        Assert.False(Remote(await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("password").GetBoolean());

        static JsonElement Remote(JsonElement s) => s.GetProperty("editable").GetProperty("remotes").GetProperty("list")[0];
    }

    // ---------- the inbound webhooks' token ----------

    [Fact]
    public async Task The_webhook_token_opens_the_webhooks_and_nothing_else()
    {
        using var app = new BamfApp();
        var c = app.Client();
        await SetMain(c);
        var make = new HttpRequestMessage(HttpMethod.Post, "/api/settings/hooktoken") { Content = JsonContent.Create(new { action = "generate" }) };
        make.Headers.Authorization = BamfApp.Basic(Main);
        var token = (await Json(await c.SendAsync(make))).GetProperty("token").GetString()!;

        var s = app.Client();
        HttpRequestMessage Hook(string url, string? t)
        {
            var m = new HttpRequestMessage(HttpMethod.Post, url);
            if (t is not null) m.Headers.Add("X-BAMF-Token", t);
            return m;
        }
        Assert.Equal(HttpStatusCode.OK, (await s.SendAsync(Hook("/api/hooks/scan", token))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.SendAsync(Hook("/api/hooks/scan?token=" + token, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.SendAsync(Hook("/api/hooks/scan", "not-the-token"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.SendAsync(Hook("/api/scan", token))).StatusCode);
        var settings = new HttpRequestMessage(HttpMethod.Get, "/api/settings");
        settings.Headers.Add("X-BAMF-Token", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.SendAsync(settings)).StatusCode);
        Assert.DoesNotContain(token, await (await c.SendAsync(Get("/api/settings", Main))).Content.ReadAsStringAsync());
    }

    // ---------- HTTPS ----------

    [Fact]
    public async Task A_certificate_made_in_settings_waits_for_a_restart_and_downloads_without_its_key()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var https = await Json(await Post(c, "/api/settings/https", new { action = "create" }));
        Assert.Equal("restart", https.GetProperty("state").GetString());
        Assert.True(File.Exists(Path.Combine(app.Dir, "bamf-https.pfx")));
        var crt = await c.GetByteArrayAsync("/api/settings/https/certificate");
        using var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(crt);
        Assert.False(cert.HasPrivateKey);
        Assert.Equal("off", (await Json(await Post(c, "/api/settings/https", new { action = "remove" }))).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/settings/https/certificate")).StatusCode);
    }
}
