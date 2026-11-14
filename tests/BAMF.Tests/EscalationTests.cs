using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>Acknowledging alerts, and sending on the ones nobody does: once, to the one destination, only what was raised after it was switched on.</summary>
[Collection("Endpoints")]
public class EscalationTests
{
    private sealed class Recorder : IHttpClientFactory
    {
        public readonly List<(string Url, string Body)> Sent = new();
        public HttpClient CreateClient(string name) => new(new Handler(this));
        private sealed class Handler(Recorder owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
                lock (owner.Sent) owner.Sent.Add((request.RequestUri!.ToString(), body));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }
    }

    private sealed record Rig(WebApplicationFactory<Program> Host, HostStore Store, ScannerService Scanner, AlertEscalation Esc, Recorder Http, HttpClient Client, string PhoneId) : IDisposable
    {
        public void Dispose() => Host.Dispose();
    }

    private static Rig Start(BamfApp app)
    {
        var http = new Recorder();
        var host = app.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IHttpClientFactory>(http)));
        var store = host.Services.GetRequiredService<HostStore>();
        var scanner = host.Services.GetRequiredService<ScannerService>();
        store.SetSetting("webhookUrl", "https://main.example/hook");
        Assert.Null(scanner.SaveExtraDestinations(new[] { new ScannerService.DestinationInput(null, "Phone", "https://phone.example/hook", "json", new List<string> { "reports" }) }));
        return new Rig(host, store, scanner, host.Services.GetRequiredService<AlertEscalation>(), http, host.CreateClient(), scanner.ExtraDestinations[0].Id);
    }

    private static async Task On(Rig r, params string[] kinds)
    {
        var res = await r.Client.PostAsJsonAsync("/api/settings/escalation", new { to = r.PhoneId, minutes = 15, kinds = kinds.Length == 0 ? new[] { "security" } : kinds });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var on = await r.Client.PostAsJsonAsync("/api/settings/escalation", new { enabled = true });
        Assert.True(on.IsSuccessStatusCode, await on.Content.ReadAsStringAsync());
    }

    private static List<(string Url, string Body)> Sent(Rig r) { lock (r.Http.Sent) return r.Http.Sent.ToList(); }

    [Fact]
    public async Task An_unacknowledged_alert_goes_to_the_chosen_destination_alone_after_the_wait_and_only_once()
    {
        using var app = new BamfApp();
        using var r = Start(app);
        await On(r);
        r.Store.AddAlert("security", "New DHCP server 10.0.0.9", "It offered an address.");
        var t0 = DateTime.UtcNow;

        Assert.Equal(0, await r.Esc.Run(t0.AddMinutes(5), default));
        Assert.Empty(Sent(r));
        Assert.Equal(1, await r.Esc.Run(t0.AddMinutes(16), default));
        var sent = Sent(r);
        Assert.Single(sent);
        Assert.Equal("https://phone.example/hook", sent[0].Url);                      // not the main webhook, and whatever kinds the phone takes
        Assert.Contains("Not acknowledged after 15 min: New DHCP server 10.0.0.9", sent[0].Body);
        Assert.Equal(0, await r.Esc.Run(t0.AddMinutes(30), default));                  // once
        Assert.Single(Sent(r));
    }

    [Fact]
    public async Task An_acknowledged_alert_is_never_sent_on_and_the_list_says_so()
    {
        using var app = new BamfApp();
        using var r = Start(app);
        await On(r);
        r.Store.AddAlert("security", "New open port on camera: 23", "Telnet.");
        var list = await r.Client.GetFromJsonAsync<JsonElement>("/api/alerts");
        var key = list[0].GetProperty("key").GetString()!;
        Assert.Equal(JsonValueKind.Null, list[0].GetProperty("acked").ValueKind);
        Assert.Equal(1, (await r.Client.GetFromJsonAsync<JsonElement>("/api/escalation")).GetProperty("waiting").GetInt32());

        var ack = await r.Client.PostAsJsonAsync("/api/alerts/ack", new { key });
        Assert.Equal(1, (await ack.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("acknowledged").GetInt32());
        Assert.Equal(JsonValueKind.String, (await r.Client.GetFromJsonAsync<JsonElement>("/api/alerts"))[0].GetProperty("acked").ValueKind);
        Assert.Equal(0, (await r.Client.GetFromJsonAsync<JsonElement>("/api/escalation")).GetProperty("waiting").GetInt32());

        Assert.Equal(0, await r.Esc.Run(DateTime.UtcNow.AddMinutes(30), default));
        Assert.Empty(Sent(r));
    }

    [Fact]
    public async Task Acknowledge_all_covers_every_alert_showing()
    {
        using var app = new BamfApp();
        using var r = Start(app);
        await On(r);
        for (var i = 0; i < 3; i++) r.Store.AddAlert("security", "Alert " + i, "x");
        var res = await r.Client.PostAsJsonAsync("/api/alerts/ack", new { all = true });
        Assert.Equal(3, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("acknowledged").GetInt32());
        Assert.Equal(0, await r.Esc.Run(DateTime.UtcNow.AddMinutes(30), default));
        Assert.Equal(HttpStatusCode.BadRequest, (await r.Client.PostAsJsonAsync("/api/alerts/ack", new { })).StatusCode);
    }

    [Fact]
    public async Task Alerts_raised_before_it_was_switched_on_are_not_sent()
    {
        using var app = new BamfApp();
        using var r = Start(app);
        r.Store.AddAlert("security", "Yesterday's alert", "x");
        await Task.Delay(30);
        await On(r);
        Assert.Equal(0, await r.Esc.Run(DateTime.UtcNow.AddMinutes(30), default));
        Assert.Empty(Sent(r));
    }

    [Fact]
    public async Task Only_the_ticked_groups_count_and_a_wake_or_a_snoozed_alert_never_does()
    {
        using var app = new BamfApp();
        using var r = Start(app);
        await On(r, "security");
        r.Store.AddAlert("rule", "NAS is down", "x");
        r.Store.AddAlert("wake", "Woke the TV", "x");
        r.Store.AddAlert("port", "New open port on the TV: 23", "Telnet. Not sent: it's snoozed.");
        Assert.Equal(0, await r.Esc.Run(DateTime.UtcNow.AddMinutes(30), default));

        await r.Client.PostAsJsonAsync("/api/settings/escalation", new { kinds = new[] { "security", "status" } });
        Assert.Equal(1, await r.Esc.Run(DateTime.UtcNow.AddMinutes(30), default));          // the rule alert now; never the wake or the snoozed one
        Assert.Contains("NAS is down", Sent(r)[0].Body);
    }

    [Fact]
    public async Task It_is_quiet_during_a_pause_and_does_not_send_a_stale_alert_afterwards()
    {
        using var app = new BamfApp();
        using var r = Start(app);
        await On(r);
        r.Store.AddAlert("security", "Fresh", "x");
        await r.Scanner.PauseAlerts(60, default);
        Assert.Equal(0, await r.Esc.Run(DateTime.UtcNow.AddMinutes(30), default));
        await r.Scanner.PauseAlerts(0, default);
        Assert.Equal(0, await r.Esc.Run(DateTime.UtcNow.AddHours(7), default));            // far too old to be worth sending
        Assert.Equal(1, await r.Esc.Run(DateTime.UtcNow.AddMinutes(30), default));
    }

    [Fact]
    public async Task A_flood_goes_out_ten_at_a_time()
    {
        using var app = new BamfApp();
        using var r = Start(app);
        await On(r);
        for (var i = 0; i < 14; i++) r.Store.AddAlert("security", "Alert " + i, "x");
        var later = DateTime.UtcNow.AddMinutes(30);
        Assert.Equal(10, await r.Esc.Run(later, default));
        Assert.Equal(4, await r.Esc.Run(later, default));
        Assert.Equal(0, await r.Esc.Run(later, default));
    }

    [Fact]
    public async Task It_will_not_switch_on_without_somewhere_to_send_and_refuses_nonsense()
    {
        using var app = new BamfApp();
        using var r = Start(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await r.Client.PostAsJsonAsync("/api/settings/escalation", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await r.Client.PostAsJsonAsync("/api/settings/escalation", new { to = "nowhere" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await r.Client.PostAsJsonAsync("/api/settings/escalation", new { minutes = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await r.Client.PostAsJsonAsync("/api/settings/escalation", new { minutes = 5000 })).StatusCode);
        await r.Client.PostAsJsonAsync("/api/settings/escalation", new { to = r.PhoneId, kinds = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, (await r.Client.PostAsJsonAsync("/api/settings/escalation", new { enabled = true })).StatusCode);   // nothing ticked
        var d = await r.Client.GetFromJsonAsync<JsonElement>("/api/escalation");
        Assert.False(d.GetProperty("enabled").GetBoolean());
        Assert.Equal("Phone", d.GetProperty("toName").GetString());
    }

    [Fact]
    public async Task Clearing_the_alerts_clears_their_marks()
    {
        using var app = new BamfApp();
        using var r = Start(app);
        r.Store.AddAlert("security", "One", "x");
        await r.Client.PostAsJsonAsync("/api/alerts/ack", new { all = true });
        Assert.Single(r.Store.AlertMarks());
        r.Store.ClearAlerts();
        Assert.Empty(r.Store.AlertMarks());
    }
}
