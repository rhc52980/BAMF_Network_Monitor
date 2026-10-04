using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>
/// "Look it up": finding the public address on demand. What counts as an answer, what's said when there isn't
/// one, and the endpoint behind the button, with Cloudflare standing in as a fake so nothing is sent anywhere.
/// </summary>
[Collection("Endpoints")]
public class ExternalIpLookupTests
{
    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls;
        public string? Url, Agent;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Url = request.RequestUri?.ToString(); Agent = request.Headers.UserAgent.ToString();
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Says(string? ip, HttpStatusCode code = HttpStatusCode.OK)
    {
        var r = new HttpResponseMessage(code);
        if (ip is not null) r.Headers.Add("cf-meta-ip", ip);
        return r;
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(h, false); }

    [Fact]
    public async Task It_asks_cloudflare_for_a_file_of_no_bytes_and_takes_the_address_it_names()
    {
        var fake = new Fake(_ => Says("203.0.113.9"));
        var (ip, error) = await ExternalIpLookup.Find(new HttpClient(fake), default);
        Assert.Equal("203.0.113.9", ip);
        Assert.Null(error);
        Assert.Equal("https://speed.cloudflare.com/__down?bytes=0", fake.Url);
        Assert.Equal("BAMF", fake.Agent);
    }

    [Fact]
    public async Task An_answer_without_an_address_says_so()
    {
        var (ip, error) = await ExternalIpLookup.Find(new HttpClient(new Fake(_ => Says(null))), default);
        Assert.Null(ip);
        Assert.Contains("didn't say", error);
    }

    [Fact]
    public async Task An_error_from_cloudflare_is_reported_with_its_number()
    {
        var (ip, error) = await ExternalIpLookup.Find(new HttpClient(new Fake(_ => Says("203.0.113.9", HttpStatusCode.ServiceUnavailable))), default);
        Assert.Null(ip);
        Assert.Contains("503", error);
    }

    [Fact]
    public async Task No_connection_is_reported_not_thrown()
    {
        var (ip, error) = await ExternalIpLookup.Find(new HttpClient(new Fake(_ => throw new HttpRequestException("no route to host"))), default);
        Assert.Null(ip);
        Assert.Contains("no route to host", error);
    }

    private static HttpClient AppWith(BamfApp app, Fake fake) =>
        app.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            foreach (var d in s.Where(d => d.ServiceType == typeof(IHttpClientFactory)).ToList()) s.Remove(d);
            s.AddSingleton<IHttpClientFactory>(new Factory(fake));
        })).CreateClient();

    [Fact]
    public async Task The_button_records_the_address_as_a_lookup()
    {
        using var app = new BamfApp();
        var c = AppWith(app, new Fake(_ => Says("198.51.100.20")));

        var r = await c.PostAsync("/api/externalip/lookup", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("198.51.100.20", j.GetProperty("externalIp").GetProperty("ip").GetString());
        Assert.Equal("lookup", j.GetProperty("externalIp").GetProperty("source").GetString());
        Assert.False(j.GetProperty("changed").GetBoolean());

        var wan = await c.GetFromJsonAsync<JsonElement>("/api/wan");
        Assert.Equal("198.51.100.20", wan.GetProperty("externalIp").GetProperty("ip").GetString());
    }

    [Fact]
    public async Task Pressing_it_twice_in_a_moment_asks_cloudflare_once()
    {
        using var app = new BamfApp();
        var fake = new Fake(_ => Says("198.51.100.20"));
        var c = AppWith(app, fake);
        await c.PostAsync("/api/externalip/lookup", null);
        var again = await c.PostAsync("/api/externalip/lookup", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task A_failed_lookup_is_an_error_and_keeps_what_was_known()
    {
        using var app = new BamfApp();
        app.Services.GetRequiredService<HostStore>().RecordExternalIp("203.0.113.9", "speedtest");
        var c = AppWith(app, new Fake(_ => Says(null, HttpStatusCode.BadGateway)));
        var r = await c.PostAsync("/api/externalip/lookup", null);
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Contains("502", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal("203.0.113.9", app.Services.GetRequiredService<HostStore>().GetExternalIp()!.Ip);
    }

    [Fact]
    public async Task An_answer_that_isnt_a_public_address_is_refused()
    {
        using var app = new BamfApp();
        var c = AppWith(app, new Fake(_ => Says("192.168.1.20")));
        var r = await c.PostAsync("/api/externalip/lookup", null);
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Contains("isn't a public address", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Null(app.Services.GetRequiredService<HostStore>().GetExternalIp());
    }

    [Fact]
    public async Task A_new_address_is_reported_as_a_change()
    {
        using var app = new BamfApp();
        app.Services.GetRequiredService<HostStore>().RecordExternalIp("203.0.113.9", "speedtest");
        var c = AppWith(app, new Fake(_ => Says("198.51.100.20")));
        var j = await (await c.PostAsync("/api/externalip/lookup", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(j.GetProperty("changed").GetBoolean());
        Assert.Equal("203.0.113.9", j.GetProperty("externalIp").GetProperty("previous").GetString());
    }
}
