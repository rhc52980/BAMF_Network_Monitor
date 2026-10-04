using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>The heartbeat: which addresses it accepts, that the saved address never comes back out, that it can be switched off, and that a visit's outcome is recorded.</summary>
[Collection("Endpoints")]
public class HeartbeatTests
{
    private const string Secret = "https://hc-ping.example/3f9a1b2c-SECRET-uuid";

    [Theory]
    [InlineData("https://hc-ping.com/abc", true)]
    [InlineData("http://192.168.1.20:3001/api/push/xyz?status=up", true)]
    [InlineData("", false)]
    [InlineData("hc-ping.com/abc", false)]
    [InlineData("ftp://hc-ping.com/abc", false)]
    [InlineData("javascript:alert(1)", false)]
    public void Only_a_web_address_is_accepted(string url, bool ok) => Assert.Equal(ok, Heartbeat.Check(url) is null);

    [Fact]
    public void Only_where_it_goes_is_ever_shown()
    {
        Assert.Equal("https://hc-ping.example/…", Heartbeat.Masked(Secret));
        Assert.Equal("http://10.0.0.5:3001/…", Heartbeat.Masked("http://10.0.0.5:3001/api/push/SECRET?x=1"));
        Assert.Null(Heartbeat.Masked("nonsense"));
    }

    [Fact]
    public async Task Saved_it_is_never_in_what_the_dashboard_is_sent_nor_in_the_settings_file()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var r = await c.PostAsJsonAsync("/api/settings/heartbeat", new { enabled = false, url = Secret, minutes = 10 });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        foreach (var path in new[] { "/api/settings/heartbeat", "/api/connections", "/api/settings", "/api/settings/export", "/api/hosts" })
        {
            var body = await c.GetStringAsync(path);
            Assert.DoesNotContain("SECRET", body);
            Assert.DoesNotContain("3f9a1b2c", body);
        }
        var j = await c.GetFromJsonAsync<JsonElement>("/api/settings/heartbeat");
        Assert.True(j.GetProperty("configured").GetBoolean());
        Assert.Equal("https://hc-ping.example/…", j.GetProperty("masked").GetString());
        Assert.Equal(10, j.GetProperty("minutes").GetInt32());
    }

    [Fact]
    public async Task It_is_off_until_switched_on_and_cannot_be_switched_on_without_an_address()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var items = (await c.GetFromJsonAsync<JsonElement>("/api/connections")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("off", items.Single(i => i.GetProperty("id").GetString() == "heartbeat").GetProperty("state").GetString());

        var bad = await c.PostAsJsonAsync("/api/settings/heartbeat", new { enabled = true, minutes = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.False(app.Services.GetRequiredService<Heartbeat>().Enabled);

        Assert.True((await c.PostAsJsonAsync("/api/settings/heartbeat", new { enabled = true, url = Secret, minutes = 5 })).IsSuccessStatusCode);
        Assert.True(app.Services.GetRequiredService<Heartbeat>().Active);
        Assert.True((await c.PostAsJsonAsync("/api/settings/heartbeat", new { enabled = false, minutes = 5 })).IsSuccessStatusCode);
        var beat = app.Services.GetRequiredService<Heartbeat>();
        Assert.False(beat.Active);
        Assert.NotNull(beat.Url);        // switching off keeps the address, so switching on again needs nothing typed
    }

    [Fact]
    public async Task The_interval_is_limited_and_a_bad_address_is_refused()
    {
        using var app = new BamfApp();
        var c = app.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/heartbeat", new { enabled = false, url = Secret, minutes = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/heartbeat", new { enabled = false, url = Secret, minutes = 1441 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/heartbeat", new { enabled = false, url = "not an address", minutes = 5 })).StatusCode);
    }

    [Fact]
    public async Task Clearing_the_address_forgets_it_and_leaving_it_out_keeps_it()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var beat = app.Services.GetRequiredService<Heartbeat>();
        await c.PostAsJsonAsync("/api/settings/heartbeat", new { enabled = false, url = Secret, minutes = 5 });
        await c.PostAsJsonAsync("/api/settings/heartbeat", new { enabled = false, minutes = 7 });
        Assert.Equal(Secret, beat.Url);
        Assert.Equal(7, beat.Minutes);
        await c.PostAsJsonAsync("/api/settings/heartbeat", new { enabled = false, url = "", minutes = 7 });
        Assert.Null(beat.Url);
    }

    [Fact]
    public async Task A_visit_reaches_the_address_and_its_outcome_is_recorded_either_way()
    {
        var hits = new List<string>();
        var factory = new FakeFactory(req => { hits.Add(req.RequestUri!.ToString()); return req.RequestUri!.AbsolutePath.EndsWith("/fail") ? HttpStatusCode.BadGateway : HttpStatusCode.OK; });
        using var app = new BamfApp().WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IHttpClientFactory>(factory)));
        var store = app.Services.GetRequiredService<HostStore>();
        var beat = app.Services.GetRequiredService<Heartbeat>();

        store.SetSetting("heartbeatUrl", "https://hc-ping.example/abc");
        var ok = await beat.VisitNow(default);
        Assert.True(ok.Ok);
        Assert.Equal(["https://hc-ping.example/abc"], hits);
        Assert.True(beat.Last.Ok);

        store.SetSetting("heartbeatUrl", "https://hc-ping.example/fail");
        var bad = await beat.VisitNow(default);
        Assert.False(bad.Ok);
        Assert.Contains("502", bad.Error);
        Assert.False(beat.Last.Ok);
    }

    private sealed class FakeFactory(Func<HttpRequestMessage, HttpStatusCode> answer) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(answer));
        private sealed class Handler(Func<HttpRequestMessage, HttpStatusCode> answer) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(answer(request)));
        }
    }
}
