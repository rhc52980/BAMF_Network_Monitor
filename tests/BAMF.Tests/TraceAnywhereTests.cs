using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>Ping, trace and path ping to an address that was typed: what is accepted, what is not, names looked up, the gap, the off switch, and path ping's per-hop numbers.</summary>
[Collection("Endpoints")]
public class TraceAnywhereTests
{
    private static readonly DateTime T = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Fake(Func<IPAddress, int, NetworkTools.Probe> answer) : NetworkTools.IProbe
    {
        public readonly List<(string Target, int Ttl)> Sent = new();
        public Task<NetworkTools.Probe> SendAsync(IPAddress target, int timeoutMs, int ttl, CancellationToken ct)
        {
            lock (Sent) Sent.Add((target.ToString(), ttl));
            return Task.FromResult(answer(target, ttl));
        }
    }

    private static NetworkTools.Probe Ok(long ms) => new(IPStatus.Success, ms, null);
    private static NetworkTools.Probe Hop(string from, long ms) => new(IPStatus.TtlExpired, ms, IPAddress.Parse(from));
    private static NetworkTools.Probe Lost => new(IPStatus.TimedOut, 0, null);

    // ---------- what can be typed ----------

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("192.168.1.1")]                  // private addresses are allowed
    [InlineData("10.0.0.5")]
    [InlineData("172.16.4.9")]
    [InlineData("100.64.0.1")]
    [InlineData("example.com")]
    [InlineData("nas.local")]
    [InlineData("my-router_2.home")]
    [InlineData("  8.8.4.4  ")]
    public void These_are_accepted(string input) => Assert.Null(NetworkTools.CheckTarget(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("999.1.1.1")]
    [InlineData("example.com; rm -rf /")]
    [InlineData("a b")]
    [InlineData("http://example.com")]
    [InlineData("-bad.com")]
    [InlineData("bad..com")]
    [InlineData(".bad.com")]
    [InlineData("exa$mple.com")]
    public void These_are_refused(string input) => Assert.NotNull(NetworkTools.CheckTarget(input));

    [Fact]
    public void A_name_that_is_too_long_is_refused() => Assert.NotNull(NetworkTools.CheckTarget(new string('a', 254)));

    // ---------- running them ----------

    [Fact]
    public async Task A_private_address_not_on_any_list_is_traced_and_every_probe_goes_to_it_and_nowhere_else()
    {
        var fake = new Fake((a, ttl) => ttl == 1 ? Hop("192.168.1.1", 1) : Ok(6));
        var r = await new NetworkTools(fake).TraceAnywhere("10.20.30.40", default, T);
        Assert.True(r!.Ok);
        Assert.Equal("10.20.30.40", r.Target);
        Assert.All(fake.Sent, s => Assert.Equal("10.20.30.40", s.Target));
        Assert.Equal([1, 2], fake.Sent.Select(s => s.Ttl).ToArray());
    }

    [Fact]
    public async Task A_name_is_looked_up_once_and_its_first_IPv4_address_is_used_and_said()
    {
        var fake = new Fake((_, _) => Ok(5));
        var tools = new NetworkTools(fake, (name, _) => Task.FromResult(new[] { IPAddress.Parse("2001:db8::9"), IPAddress.Parse("93.184.216.34"), IPAddress.Parse("93.184.216.35") }));
        var r = await tools.RunAnywhere("ping", "example.com", default, T);
        Assert.All(fake.Sent, s => Assert.Equal("93.184.216.34", s.Target));
        Assert.Equal("example.com (93.184.216.34)", r!.Target);
        Assert.Contains("example.com is 93.184.216.34 (it has 2 addresses; the first is used)", r.Lines[0]);
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_or_has_no_IPv4_or_points_somewhere_it_should_not_is_refused_with_a_reason()
    {
        var none = new NetworkTools(new Fake((_, _) => Ok(1)), (_, _) => throw new System.Net.Sockets.SocketException(11001));
        Assert.Contains("doesn't resolve", (await Assert.ThrowsAsync<ArgumentException>(() => none.RunAnywhere("ping", "nope.invalid", default, T))).Message);

        var v6 = new NetworkTools(new Fake((_, _) => Ok(1)), (_, _) => Task.FromResult(new[] { IPAddress.Parse("2001:db8::1") }));
        Assert.Contains("no IPv4 address", (await Assert.ThrowsAsync<ArgumentException>(() => v6.RunAnywhere("ping", "v6only.example", default, T))).Message);

        var loop = new NetworkTools(new Fake((_, _) => Ok(1)), (_, _) => Task.FromResult(new[] { IPAddress.Parse("127.0.0.1") }));
        Assert.Contains("points at 127.0.0.1", (await Assert.ThrowsAsync<ArgumentException>(() => loop.RunAnywhere("ping", "sneaky.example", default, T))).Message);
    }

    [Fact]
    public async Task Nothing_is_sent_when_the_target_is_refused()
    {
        var fake = new Fake((_, _) => Ok(1));
        await Assert.ThrowsAsync<ArgumentException>(() => new NetworkTools(fake).RunAnywhere("trace", "8.8.8.8; calc", default, T));
        Assert.Empty(fake.Sent);
    }

    [Fact]
    public async Task A_second_one_too_soon_is_told_to_wait_and_one_after_the_gap_goes_through()
    {
        var tools = new NetworkTools(new Fake((_, _) => Ok(2)));
        Assert.NotNull(await tools.RunAnywhere("ping", "8.8.8.8", default, T));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.RunAnywhere("ping", "8.8.4.4", default, T.AddSeconds(3)));
        Assert.Contains("Try again in", ex.Message);
        Assert.NotNull(await tools.RunAnywhere("ping", "8.8.4.4", default, T.AddSeconds(9)));
    }

    [Fact]
    public async Task An_unknown_tool_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new NetworkTools(new Fake((_, _) => Ok(1))).RunAnywhere("nmap", "8.8.8.8", default, T));
    }

    // ---------- path ping ----------

    [Fact]
    public async Task Path_ping_pings_every_router_that_answered_the_trace_and_says_where_loss_starts()
    {
        var calls = new Dictionary<string, int>();
        var fake = new Fake((a, ttl) =>
        {
            var key = a.ToString();
            if (ttl < 128)                                         // the trace
                return ttl switch { 1 => Hop("192.168.1.1", 1), 2 => Hop("10.0.0.1", 9), _ => Ok(20) };
            calls[key] = calls.GetValueOrDefault(key) + 1;         // the pings of each hop
            return key switch
            {
                "192.168.1.1" => Ok(1),
                "10.0.0.1" => calls[key] % 2 == 0 ? Lost : Ok(10),  // half lost
                _ => Ok(21),
            };
        });
        var r = await new NetworkTools(fake).PathPing("8.8.8.8", default);
        Assert.True(r.Ok);
        Assert.Contains(r.Lines, l => l.Contains("192.168.1.1") && l.Contains("0%"));
        Assert.Contains(r.Lines, l => l.Contains("10.0.0.1") && l.Contains("50%"));
        Assert.Contains(r.Lines, l => l.Contains("8.8.8.8") && l.Contains("0%"));
        Assert.Contains("first loss is at hop 2 (50%)", r.Summary);
        Assert.Equal(3 * NetworkTools.PathPings, fake.Sent.Count(s => s.Ttl == 128));
    }

    [Fact]
    public async Task A_router_that_answers_a_trace_but_never_a_ping_is_called_silent_not_lossy()
    {
        var fake = new Fake((a, ttl) => ttl < 128 ? (ttl == 1 ? Hop("10.9.9.9", 2) : Ok(7)) : a.ToString() == "10.9.9.9" ? Lost : Ok(7));
        var r = await new NetworkTools(fake).PathPing("8.8.8.8", default);
        Assert.Contains(r.Lines, l => l.Contains("10.9.9.9") && l.Contains("silent"));
        Assert.Contains("none lost a ping", r.Summary);
    }

    [Fact]
    public async Task A_trace_that_gets_no_answer_at_all_has_no_hops_to_ping_and_says_so()
    {
        var r = await new NetworkTools(new Fake((_, _) => Lost)).PathPing("203.0.113.9", default);
        Assert.Contains("No hops to ping", r.Summary);
        Assert.Contains(r.Lines, l => l.Contains("No router answered"));
    }

    // ---------- the endpoint and its switches ----------

    private static WebApplicationFactory<Program> WithFake(BamfApp app, NetworkTools tools) =>
        app.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton(tools)));

    [Fact]
    public async Task It_is_off_until_switched_on_and_then_works_for_an_outside_address()
    {
        var fake = new Fake((_, ttl) => ttl == 1 ? Hop("192.168.1.1", 1) : Ok(8));
        using var app = new BamfApp();
        using var host = WithFake(app, new NetworkTools(fake));
        var c = host.CreateClient();
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/hosts")).GetProperty("traceAnywhereEnabled").GetBoolean());
        var off = await c.PostAsJsonAsync("/api/tools/anywhere", new { tool = "trace", target = "8.8.8.8" });
        Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
        Assert.Empty(fake.Sent);

        Assert.True((await c.PostAsJsonAsync("/api/settings/trace-anywhere", new { enabled = true })).IsSuccessStatusCode);
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/hosts")).GetProperty("traceAnywhereEnabled").GetBoolean());
        var r = await c.PostAsJsonAsync("/api/tools/anywhere", new { tool = "trace", target = "8.8.8.8" });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("8.8.8.8", j.GetProperty("target").GetString());
        Assert.True(j.GetProperty("ok").GetBoolean());
        Assert.All(fake.Sent, s => Assert.Equal("8.8.8.8", s.Target));
    }

    [Fact]
    public async Task A_bad_target_is_a_400_with_the_reason_and_too_soon_is_a_429()
    {
        using var app = new BamfApp();
        using var host = WithFake(app, new NetworkTools(new Fake((_, _) => Ok(1))));
        var c = host.CreateClient();
        await c.PostAsJsonAsync("/api/settings/trace-anywhere", new { enabled = true });
        var bad = await c.PostAsJsonAsync("/api/tools/anywhere", new { tool = "ping", target = "1.2.3.4; dir" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("letters, digits", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/tools/anywhere", new { tool = "nmap", target = "8.8.8.8" })).StatusCode);

        Assert.True((await c.PostAsJsonAsync("/api/tools/anywhere", new { tool = "ping", target = "8.8.8.8" })).IsSuccessStatusCode);
        Assert.Equal((HttpStatusCode)429, (await c.PostAsJsonAsync("/api/tools/anywhere", new { tool = "ping", target = "8.8.4.4" })).StatusCode);
    }

    [Fact]
    public async Task Switching_the_network_tools_off_takes_this_with_it_and_the_device_menu_gets_path_ping()
    {
        var fake = new Fake((_, ttl) => ttl < 128 ? Ok(3) : Ok(2));
        using var app = new BamfApp();
        using var host = WithFake(app, new NetworkTools(fake));
        var c = host.CreateClient();
        await c.PostAsJsonAsync("/api/settings/trace-anywhere", new { enabled = true });
        await c.PostAsJsonAsync("/api/settings/network-tools", new { enabled = false });
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/hosts")).GetProperty("traceAnywhereEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/tools/anywhere", new { tool = "ping", target = "8.8.8.8" })).StatusCode);
        await c.PostAsJsonAsync("/api/settings/network-tools", new { enabled = true });

        var store = host.Services.GetRequiredService<HostStore>();
        store.UpsertSeen("cc:00:00:00:09:01", "192.168.30.90", "dev90", "Acme", "192.168.30.0/24");
        var id = store.GetAll().Single(h => h.Mac == "cc:00:00:00:09:01").Id;
        var r = await c.PostAsJsonAsync($"/api/hosts/{id}/tool", new { tool = "path" });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Equal("path", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tool").GetString());
        Assert.Equal("Ping and trace route to any address", SettingsLog.Label("POST", "/api/settings/trace-anywhere"));
    }
}
