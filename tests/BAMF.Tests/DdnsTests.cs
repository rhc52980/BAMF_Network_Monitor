using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>The dynamic DNS hook: it calls your URL when the public address isn't the one last updated, keeps trying until it is accepted, and never shows the URL again.</summary>
[Collection("Endpoints")]
public class DdnsTests
{
    private const string Url = "https://www.example-dns.test/update?domains=home&token=SECRET123&ip={ip}";

    private sealed class Rig
    {
        public string Ip = "198.51.100.7";
        public int Status = 200;
        public string Body = "good";
        public readonly List<string> Calls = new();
        public readonly List<string?> Logins = new();
    }

    private static (DdnsUpdater D, HostStore Store, Rig R) Set(BamfApp app, bool withUrl = true)
    {
        var d = app.Services.GetRequiredService<DdnsUpdater>();
        var store = app.Services.GetRequiredService<HostStore>();
        var r = new Rig();
        d.Lookup = _ => Task.FromResult(((string?)r.Ip, (string?)null));
        d.Send = (req, _) => { r.Calls.Add(req.RequestUri!.ToString()); r.Logins.Add(req.Headers.Authorization?.ToString()); return Task.FromResult((r.Status, r.Body)); };
        if (withUrl) store.SetSetting("ddnsUrl", Url);
        return (d, store, r);
    }

    [Fact]
    public void The_address_goes_where_ip_was_written_and_a_login_in_the_url_becomes_a_header()
    {
        using var req = DdnsUpdater.Build("https://dyn.test/nic/update?hostname=home&myip={ip}", "198.51.100.7");
        Assert.Equal("https://dyn.test/nic/update?hostname=home&myip=198.51.100.7", req.RequestUri!.ToString());
        Assert.Null(req.Headers.Authorization);

        using var login = DdnsUpdater.Build("https://me%40mail.test:p%3Fss@dyn.test/nic/update?myip={ip}", "198.51.100.7");
        Assert.Equal("https://dyn.test/nic/update?myip=198.51.100.7", login.RequestUri!.ToString());
        Assert.Equal("Basic", login.Headers.Authorization!.Scheme);
        Assert.Equal("me@mail.test:p?ss", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(login.Headers.Authorization.Parameter!)));

        using var plain = DdnsUpdater.Build("https://dyn.test/update?token=abc", "198.51.100.7");   // a provider that works the address out itself
        Assert.Equal("https://dyn.test/update?token=abc", plain.RequestUri!.ToString());
    }

    [Fact]
    public void Only_where_the_url_goes_is_ever_shown_and_only_http_and_https_are_accepted()
    {
        Assert.Equal("https://www.example-dns.test/…", DdnsUpdater.Mask(Url));
        Assert.DoesNotContain("SECRET123", DdnsUpdater.Mask(Url));
        Assert.Equal("http://dyn.test:8080/…", DdnsUpdater.Mask("http://dyn.test:8080/x"));
        Assert.Null(DdnsUpdater.Validate(Url));
        Assert.NotNull(DdnsUpdater.Validate("ftp://dyn.test/x"));
        Assert.NotNull(DdnsUpdater.Validate("not a url"));
        Assert.NotNull(DdnsUpdater.Validate("https://dyn.test/" + new string('a', 600)));
    }

    [Theory]
    [InlineData("good 198.51.100.7", false)]
    [InlineData("nochg 198.51.100.7", false)]
    [InlineData("OK", false)]
    [InlineData("", false)]
    [InlineData("badauth", true)]
    [InlineData("nohost", true)]
    [InlineData("KO", true)]
    [InlineData("abuse", true)]
    [InlineData("911 try later", true)]
    public void A_200_answer_that_says_no_in_its_body_is_a_refusal(string body, bool refused) => Assert.Equal(refused, DdnsUpdater.Refused(body));

    [Fact]
    public async Task It_updates_once_per_address_and_again_when_the_address_moves()
    {
        using var app = new BamfApp();
        var (d, store, r) = Set(app);
        var first = await d.Run(false, default);
        Assert.True(first!.Ok);
        Assert.Single(r.Calls);
        Assert.Equal("https://www.example-dns.test/update?domains=home&token=SECRET123&ip=198.51.100.7", r.Calls[0]);
        Assert.Equal("198.51.100.7", d.PushedIp);

        Assert.Null(await d.Run(false, default));            // nothing moved: nothing to do
        Assert.Single(r.Calls);

        r.Ip = "198.51.100.99";
        var moved = await d.Run(false, default);
        Assert.True(moved!.Ok);
        Assert.Equal(2, r.Calls.Count);
        Assert.EndsWith("ip=198.51.100.99", r.Calls[1]);
        Assert.Equal("198.51.100.99", d.PushedIp);
    }

    [Fact]
    public async Task A_refused_update_is_not_remembered_so_the_next_try_calls_again()
    {
        using var app = new BamfApp();
        var (d, _, r) = Set(app);
        r.Body = "badauth";
        var bad = await d.Run(false, default);
        Assert.False(bad!.Ok);
        Assert.Contains("refused", bad.Error);
        Assert.Null(d.PushedIp);

        r.Body = "good";
        Assert.True((await d.Run(false, default))!.Ok);      // the same address, tried again because it was never accepted
        Assert.Equal(2, r.Calls.Count);

        r.Status = 500;
        r.Ip = "198.51.100.8";
        var down = await d.Run(false, default);
        Assert.False(down!.Ok);
        Assert.Equal("The provider answered HTTP 500.", down.Error);
        Assert.Equal("198.51.100.7", d.PushedIp);            // still the last one it accepted
    }

    [Fact]
    public async Task Three_failures_in_a_row_raise_one_alert_and_a_success_starts_the_count_again()
    {
        using var app = new BamfApp();
        var (d, store, r) = Set(app);
        r.Status = 503;
        for (var i = 0; i < 5; i++) await d.Run(false, default);
        Assert.Single(store.GetAlerts(50).Where(a => a.Kind == "ddns"));
        Assert.DoesNotContain("SECRET123", store.GetAlerts(50).First(a => a.Kind == "ddns").Detail);

        r.Status = 200;
        await d.Run(false, default);
        r.Ip = "198.51.100.50"; r.Status = 503;
        for (var i = 0; i < 3; i++) await d.Run(false, default);
        Assert.Equal(2, store.GetAlerts(50).Count(a => a.Kind == "ddns"));
    }

    [Fact]
    public async Task Updating_now_calls_the_url_whatever_the_address_and_does_not_count_as_a_failure()
    {
        using var app = new BamfApp();
        var (d, store, r) = Set(app);
        await d.Run(false, default);
        Assert.Single(r.Calls);
        await d.Run(true, default);
        Assert.Equal(2, r.Calls.Count);
        r.Status = 500;
        for (var i = 0; i < 5; i++) await d.Run(true, default);
        Assert.Empty(store.GetAlerts(50).Where(a => a.Kind == "ddns"));
    }

    [Fact]
    public async Task Without_a_url_nothing_is_called_and_nothing_is_looked_up()
    {
        using var app = new BamfApp();
        var (d, _, r) = Set(app, withUrl: false);
        var looked = false;
        d.Lookup = _ => { looked = true; return Task.FromResult(((string?)r.Ip, (string?)null)); };
        Assert.Null(await d.Run(true, default));
        Assert.False(looked);
        Assert.Empty(r.Calls);
    }

    [Fact]
    public async Task The_api_keeps_the_url_to_itself_refuses_a_bad_one_and_updates_when_switched_on()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var (d, _, r) = Set(app, withUrl: false);

        var off = await c.GetFromJsonAsync<JsonElement>("/api/ddns");
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.False(off.GetProperty("hasUrl").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/ddns", new { enabled = true })).StatusCode);   // no URL yet
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/ddns", new { url = "ftp://dyn.test/x" })).StatusCode);

        var saved = await c.PostAsJsonAsync("/api/settings/ddns", new { url = Url });
        Assert.True(saved.IsSuccessStatusCode);
        var text = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET123", text);
        Assert.Empty(r.Calls);                                     // saving a URL calls nothing; switching on does

        var on = await c.PostAsJsonAsync("/api/settings/ddns", new { enabled = true });
        var onJson = await on.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(onJson.GetProperty("enabled").GetBoolean());
        Assert.Equal("https://www.example-dns.test/…", onJson.GetProperty("url").GetString());
        Assert.Equal("198.51.100.7", onJson.GetProperty("updated").GetString());
        Assert.True(onJson.GetProperty("last").GetProperty("ok").GetBoolean());
        Assert.Single(r.Calls);
        Assert.DoesNotContain("SECRET123", (await c.GetStringAsync("/api/ddns")));

        // a different URL is a different name: it is updated afresh
        await c.PostAsJsonAsync("/api/settings/ddns", new { url = "https://other.test/u?ip={ip}" });
        Assert.Null(d.PushedIp);

        var forgot = await (await c.PostAsJsonAsync("/api/settings/ddns", new { url = "" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(forgot.GetProperty("hasUrl").GetBoolean());
        Assert.False(forgot.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Updating_now_through_the_api_needs_a_url_and_is_limited_to_one_every_ten_seconds()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var (_, _, r) = Set(app, withUrl: false);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync("/api/ddns/run", null)).StatusCode);
        await c.PostAsJsonAsync("/api/settings/ddns", new { url = Url });
        var go = await c.PostAsync("/api/ddns/run", null);
        Assert.True(go.IsSuccessStatusCode, await go.Content.ReadAsStringAsync());
        Assert.Single(r.Calls);
        Assert.Equal((HttpStatusCode)429, (await c.PostAsync("/api/ddns/run", null)).StatusCode);
    }
}
