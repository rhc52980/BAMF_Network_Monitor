using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>Routing alerts by the tags on the device they are about: a destination for some devices, another that leaves them out.</summary>
[Collection("Endpoints")]
public class RoutingTests
{
    private sealed class Recorder : IHttpClientFactory
    {
        public readonly List<string> Urls = new();
        public HttpClient CreateClient(string name) => new(new Handler(this));
        private sealed class Handler(Recorder owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                lock (owner.Urls) owner.Urls.Add(request.RequestUri!.Host);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
        }
    }

    private sealed record Rig(WebApplicationFactory<Program> Host, HostStore Store, ScannerService Scanner, Recorder Http, HttpClient Client) : IDisposable
    {
        public void Dispose() => Host.Dispose();
        public List<string> Sent() { lock (Http.Urls) return Http.Urls.OrderBy(x => x).ToList(); }
        public void Reset() { lock (Http.Urls) Http.Urls.Clear(); }
    }

    private static Rig Start(BamfApp app)
    {
        var http = new Recorder();
        var host = app.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IHttpClientFactory>(http)));
        var store = host.Services.GetRequiredService<HostStore>();
        store.SetSetting("webhookUrl", "https://servers.example/hook");
        return new Rig(host, store, host.Services.GetRequiredService<ScannerService>(), http, host.CreateClient());
    }

    private static long Device(HostStore store, string mac, string ip, params string[] tags)
    {
        store.UpsertSeen(mac, ip, "dev-" + ip, "Acme", "192.168.2.0/24");
        var id = store.GetAll().First(h => h.Mac == mac).Id;
        if (tags.Length > 0) Assert.Null(store.SetTags(id, tags));
        return id;
    }

    private static ScannerService.Destination D(string[]? only = null, string[]? not = null) =>
        new("x", "x", "https://x.example", "auto", new[] { "status" }, only, not);

    [Fact]
    public void A_destination_with_no_tags_takes_everything_and_one_with_only_tags_takes_just_those_devices()
    {
        var kids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Kids" };
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.True(ScannerService.Routes(D(), true, kids));
        Assert.True(ScannerService.Routes(D(), false, null));
        Assert.True(ScannerService.Routes(D(only: new[] { "kids" }), true, kids));         // whatever the case
        Assert.False(ScannerService.Routes(D(only: new[] { "kids" }), true, none));
        Assert.False(ScannerService.Routes(D(only: new[] { "kids" }), false, null));        // an alert about no device in particular isn't "for the kids"
        Assert.True(ScannerService.Routes(D(only: new[] { "kids", "guests" }), true, new HashSet<string> { "guests" }));
    }

    [Fact]
    public void A_destination_that_leaves_tags_out_skips_those_devices_but_still_takes_alerts_about_no_device()
    {
        var kids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "kids", "iot" };
        Assert.False(ScannerService.Routes(D(not: new[] { "kids" }), true, kids));
        Assert.True(ScannerService.Routes(D(not: new[] { "work" }), true, kids));
        Assert.True(ScannerService.Routes(D(not: new[] { "kids" }), false, null));           // the internet being down is for everybody
        Assert.False(ScannerService.Routes(D(only: new[] { "iot" }, not: new[] { "kids" }), true, kids));   // both: in the group but excluded
    }

    [Fact]
    public async Task The_kids_devices_alert_the_phone_alone_the_servers_alert_the_main_webhook_alone_and_network_wide_alerts_go_to_the_main_one()
    {
        using var app = new BamfApp();
        using var r = Start(app);
        Assert.Null(r.Scanner.SaveExtraDestinations(new[] { new ScannerService.DestinationInput(null, "Phone", "https://phone.example/hook", "json", new List<string> { "status", "security" }, new List<string> { "kids" }, new List<string>()) }));
        r.Scanner.SetMainRoute(new List<string>(), new List<string> { "kids" });

        var tablet = Device(r.Store, "AA:00:00:00:00:01", "192.168.2.21", "kids");
        var nas = Device(r.Store, "AA:00:00:00:00:02", "192.168.2.22", "servers");
        var plain = Device(r.Store, "AA:00:00:00:00:03", "192.168.2.23");

        await r.Scanner.SendGenericAlert("Tablet is slow", "x", "status", default, hostId: tablet);
        Assert.Equal(new[] { "phone.example" }, r.Sent());
        r.Reset();
        await r.Scanner.SendGenericAlert("NAS is down", "x", "status", default, hostId: nas);
        Assert.Equal(new[] { "servers.example" }, r.Sent());
        r.Reset();
        await r.Scanner.SendGenericAlert("Something has no tags", "x", "status", default, hostId: plain);
        Assert.Equal(new[] { "servers.example" }, r.Sent());
        r.Reset();
        await r.Scanner.SendGenericAlert("The internet is down", "x", "status", default);     // about no device
        Assert.Equal(new[] { "servers.example" }, r.Sent());
    }

    [Fact]
    public async Task Tags_are_read_when_the_alert_is_sent_so_changing_one_changes_where_it_goes()
    {
        using var app = new BamfApp();
        using var r = Start(app);
        Assert.Null(r.Scanner.SaveExtraDestinations(new[] { new ScannerService.DestinationInput(null, "Phone", "https://phone.example/hook", "json", new List<string> { "status" }, new List<string> { "kids" }, null) }));
        var d = Device(r.Store, "AA:00:00:00:00:04", "192.168.2.24");
        await r.Scanner.SendGenericAlert("Slow", "x", "status", default, hostId: d);
        Assert.Equal(new[] { "servers.example" }, r.Sent());
        r.Reset();
        Assert.Null(r.Store.SetTags(d, new[] { "Kids" }));
        await r.Scanner.SendGenericAlert("Slow", "x", "status", default, hostId: d);
        Assert.Equal(new[] { "phone.example", "servers.example" }, r.Sent());                // the main webhook has no tag rule, so it still gets it
    }

    [Fact]
    public async Task A_destination_named_outright_gets_the_alert_whatever_its_tags()
    {
        using var app = new BamfApp();
        using var r = Start(app);
        Assert.Null(r.Scanner.SaveExtraDestinations(new[] { new ScannerService.DestinationInput(null, "Phone", "https://phone.example/hook", "json", new List<string> { "status" }, new List<string> { "kids" }, null) }));
        var d = Device(r.Store, "AA:00:00:00:00:05", "192.168.2.25", "work");
        await r.Scanner.SendGenericAlert("Escalated", "x", "security", default, only: r.Scanner.ExtraDestinations[0].Id, hostId: d);
        Assert.Equal(new[] { "phone.example" }, r.Sent());
    }

    [Fact]
    public async Task Quiet_hours_hold_an_alert_once_and_the_digest_goes_where_its_device_would_have()
    {
        using var app = new BamfApp();
        using var r = Start(app);
        Assert.Null(r.Scanner.SaveExtraDestinations(new[] { new ScannerService.DestinationInput(null, "Phone", "https://phone.example/hook", "json", new List<string> { "status", "reports" }, new List<string> { "kids" }, null) }));
        r.Scanner.SetMainRoute(new List<string>(), new List<string> { "kids" });
        r.Store.SetSetting("quietDigest", "true");
        var tablet = Device(r.Store, "AA:00:00:00:00:06", "192.168.2.26", "kids");
        var nas = Device(r.Store, "AA:00:00:00:00:07", "192.168.2.27", "servers");
        await r.Scanner.PauseAlerts(60, default);
        await r.Scanner.SendGenericAlert("Tablet is slow", "x", "status", default, hostId: tablet);
        await r.Scanner.SendGenericAlert("NAS is slow", "x", "status", default, hostId: nas);
        Assert.Equal(2, r.Scanner.HeldCount);
        Assert.Empty(r.Sent());
        await r.Scanner.PauseAlerts(0, default);
        Assert.Equal(new[] { "phone.example", "servers.example" }, r.Sent());                // one digest each, not both to both
    }

    [Fact]
    public async Task The_api_round_trips_tags_keeps_them_when_a_save_leaves_them_out_and_cleans_them()
    {
        using var app = new BamfApp();
        using var r = Start(app);
        var first = await r.Client.PostAsJsonAsync("/api/settings/destinations", new
        {
            destinations = new[] { new { name = "Phone", url = "https://phone.example/hook", format = "json", kinds = new[] { "status" }, tags = new[] { " Kids ", "kids", "", "Guests" }, notTags = new string[0] } },
        });
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        var dests = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("destinations");
        Assert.Equal(new[] { "Kids", "Guests" }, dests[0].GetProperty("tags").EnumerateArray().Select(x => x.GetString()).ToArray());
        var id = dests[0].GetProperty("id").GetString();

        // an older client that doesn't know about tags re-saves the list: the tags stay
        var again = await r.Client.PostAsJsonAsync("/api/settings/destinations", new { destinations = new[] { new { id, name = "Phone", format = "json", kinds = new[] { "status", "security" } } } });
        var kept = (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("destinations")[0];
        Assert.Equal(2, kept.GetProperty("tags").GetArrayLength());
        Assert.Equal(2, kept.GetProperty("kinds").GetArrayLength());

        // an empty list clears them
        var cleared = await r.Client.PostAsJsonAsync("/api/settings/destinations", new { destinations = new[] { new { id, name = "Phone", format = "json", kinds = new[] { "status" }, tags = new string[0] } } });
        Assert.Equal(0, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("destinations")[0].GetProperty("tags").GetArrayLength());

        var route = await r.Client.PostAsJsonAsync("/api/settings/webhookroute", new { tags = new string[0], notTags = new[] { "kids", "Kids" } });
        var rj = await route.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "kids" }, rj.GetProperty("notTags").EnumerateArray().Select(x => x.GetString()).ToArray());
        var settings = await r.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal(1, settings.GetProperty("editable").GetProperty("webhookNotTags").GetArrayLength());
    }
}
