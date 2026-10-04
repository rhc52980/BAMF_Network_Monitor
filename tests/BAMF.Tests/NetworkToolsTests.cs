using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>Ping, trace route and DNS lookup: what they say, that the target is always a known device, and the off switch. The network is a fake.</summary>
[Collection("Endpoints")]
public class NetworkToolsTests
{
    private sealed class Fake(Func<int, int, NetworkTools.Probe> answer) : NetworkTools.IProbe
    {
        public readonly List<(string Target, int Ttl)> Sent = new();
        private int _n;
        public Task<NetworkTools.Probe> SendAsync(IPAddress target, int timeoutMs, int ttl, CancellationToken ct)
        {
            lock (Sent) Sent.Add((target.ToString(), ttl));
            return Task.FromResult(answer(Interlocked.Increment(ref _n), ttl));
        }
    }

    private static NetworkTools.Probe Ok(long ms) => new(IPStatus.Success, ms, null);
    private static NetworkTools.Probe Lost => new(IPStatus.TimedOut, 0, null);

    [Fact]
    public async Task Ping_counts_what_answered_and_says_the_fastest_average_and_slowest()
    {
        var tools = new NetworkTools(new Fake((n, _) => n == 3 ? Lost : Ok(n * 10)));
        var r = await tools.Ping("192.168.30.10", default);
        Assert.True(r.Ok);
        Assert.Equal(4, r.Lines.Count);
        Assert.Contains("3 of 4 answered, 25% lost", r.Summary);
        Assert.Contains("10 / 23 / 40 ms", r.Summary);
        Assert.Contains("no answer", r.Lines[2]);
    }

    [Fact]
    public async Task Ping_with_no_answer_says_so_and_is_not_ok()
    {
        var r = await new NetworkTools(new Fake((_, _) => Lost)).Ping("192.168.30.10", default);
        Assert.False(r.Ok);
        Assert.Contains("No answer from 192.168.30.10: all 4 lost", r.Summary);
    }

    [Fact]
    public async Task A_trace_lists_each_router_on_the_way_and_stops_at_the_device()
    {
        var fake = new Fake((_, ttl) => ttl switch
        {
            1 => new(IPStatus.TtlExpired, 1, IPAddress.Parse("192.168.30.1")),
            2 => new(IPStatus.TtlExpired, 3, IPAddress.Parse("10.0.0.1")),
            _ => Ok(7),
        });
        var r = await new NetworkTools(fake).Trace("172.16.0.9", default);
        Assert.True(r.Ok);
        Assert.Equal(3, r.Lines.Count);
        Assert.Contains("192.168.30.1", r.Lines[0]);
        Assert.Contains("10.0.0.1", r.Lines[1]);
        Assert.Contains("(the device)", r.Lines[2]);
        Assert.Equal("Reached 172.16.0.9 in 3 hops", r.Summary);
        Assert.Equal([1, 2, 3], fake.Sent.Select(s => s.Ttl).ToArray());
    }

    [Fact]
    public async Task A_trace_that_goes_quiet_stops_after_five_silent_hops_instead_of_twenty()
    {
        var fake = new Fake((_, ttl) => ttl == 1 ? new(IPStatus.TtlExpired, 1, IPAddress.Parse("192.168.30.1")) : Lost);
        var r = await new NetworkTools(fake).Trace("172.16.0.9", default);
        Assert.False(r.Ok);
        Assert.Equal(6, fake.Sent.Count);
        Assert.Contains("Stopping", r.Lines[^1]);
        Assert.Equal("Didn't reach 172.16.0.9", r.Summary);
    }

    [Fact]
    public async Task Only_one_tool_runs_at_a_time()
    {
        var gate = new TaskCompletionSource();
        var slow = new SlowProbe(gate.Task);
        var tools = new NetworkTools(slow);
        var first = tools.RunAsync("ping", "192.168.30.10", null, default);
        await Task.Delay(100);
        Assert.Null(await tools.RunAsync("ping", "192.168.30.11", null, default));      // busy: told so, not queued
        gate.SetResult();
        Assert.NotNull(await first);
        Assert.NotNull(await tools.RunAsync("ping", "192.168.30.11", null, default));
    }

    private sealed class SlowProbe(Task gate) : NetworkTools.IProbe
    {
        public async Task<NetworkTools.Probe> SendAsync(IPAddress target, int timeoutMs, int ttl, CancellationToken ct) { await gate; return Ok(1); }
    }

    private static long Add(HostStore store, string ip)
    {
        var mac = "cc:00:00:00:00:" + (ip.Split('.')[^1].PadLeft(2, '0'));
        store.UpsertSeen(mac, ip, "nas-" + ip.Split('.')[^1], "Acme", "192.168.30.0/24");
        return store.GetAll().Single(h => h.Mac == mac).Id;
    }

    private static WebApplicationFactory<Program> WithFake(BamfApp app, NetworkTools.IProbe probe) =>
        app.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton(new NetworkTools(probe))));

    [Fact]
    public async Task The_endpoint_pings_the_devices_own_address_and_nothing_a_caller_types()
    {
        var fake = new Fake((_, _) => Ok(4));
        using var app = new BamfApp();
        using var host = WithFake(app, fake);
        var store = host.Services.GetRequiredService<HostStore>();
        var id = Add(store, "192.168.30.77");
        var r = await host.CreateClient().PostAsJsonAsync($"/api/hosts/{id}/tool", new { tool = "ping", target = "8.8.8.8", ip = "1.1.1.1" });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("192.168.30.77", j.GetProperty("target").GetString());
        Assert.All(fake.Sent, s => Assert.Equal("192.168.30.77", s.Target));
        Assert.Equal(4, j.GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task A_made_up_tool_a_missing_device_and_a_forgotten_one_are_refused()
    {
        using var app = new BamfApp();
        using var host = WithFake(app, new Fake((_, _) => Ok(1)));
        var store = host.Services.GetRequiredService<HostStore>();
        var c = host.CreateClient();
        var id = Add(store, "192.168.30.78");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/hosts/{id}/tool", new { tool = "nmap" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/hosts/999999/tool", new { tool = "ping" })).StatusCode);
        store.SetForgotten(id, true);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync($"/api/hosts/{id}/tool", new { tool = "ping" })).StatusCode);
    }

    [Fact]
    public async Task Switched_off_it_refuses_and_the_dashboard_is_told()
    {
        var fake = new Fake((_, _) => Ok(1));
        using var app = new BamfApp();
        using var host = WithFake(app, fake);
        var store = host.Services.GetRequiredService<HostStore>();
        var c = host.CreateClient();
        var id = Add(store, "192.168.30.79");
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/hosts")).GetProperty("networkToolsEnabled").GetBoolean());
        Assert.True((await c.PostAsJsonAsync("/api/settings/network-tools", new { enabled = false })).IsSuccessStatusCode);
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/hosts")).GetProperty("networkToolsEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/hosts/{id}/tool", new { tool = "ping" })).StatusCode);
        Assert.Empty(fake.Sent);
        await c.PostAsJsonAsync("/api/settings/network-tools", new { enabled = true });
        Assert.True((await c.PostAsJsonAsync($"/api/hosts/{id}/tool", new { tool = "ping" })).IsSuccessStatusCode);
    }

    [Fact]
    public async Task DNS_for_the_loopback_address_is_answered_without_a_probe()
    {
        var fake = new Fake((_, _) => Ok(1));
        var r = await new NetworkTools(fake).Dns("127.0.0.1", "localhost", default);
        Assert.Empty(fake.Sent);
        Assert.True(r.Ok);
        Assert.Contains(r.Lines, l => l.StartsWith("localhost resolves to"));
        Assert.Contains(r.Lines, l => l.Contains("includes this device's address"));
    }

    [Fact]
    public async Task A_name_with_odd_characters_is_never_looked_up()
    {
        var r = await new NetworkTools(new Fake((_, _) => Ok(1))).Dns("127.0.0.1", "bad name;rm -rf", default);
        Assert.DoesNotContain(r.Lines, l => l.Contains("rm -rf"));
    }
}
