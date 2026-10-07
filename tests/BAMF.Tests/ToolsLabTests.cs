using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>
/// The Tools tab's side: structured trace and path ping, the three-resolver DNS lookup, the HTTP check (against a real local web server), the
/// port check, the "why is it slow?" diagnosis, and the endpoints with their switches.
/// </summary>
[Collection("Endpoints")]
public class ToolsLabTests
{
    private sealed class Fake(Func<IPAddress, int, int, NetworkTools.Probe> answer) : NetworkTools.IProbe
    {
        private int _calls;
        public readonly List<(string Target, int Ttl)> Sent = new();
        public Task<NetworkTools.Probe> SendAsync(IPAddress target, int timeoutMs, int ttl, CancellationToken ct)
        {
            int n; lock (Sent) { Sent.Add((target.ToString(), ttl)); n = ++_calls; }
            return Task.FromResult(answer(target, ttl, n));
        }
    }

    private static NetworkTools.Probe Ok(long ms) => new(IPStatus.Success, ms, null);
    private static NetworkTools.Probe Hop(string from, long ms) => new(IPStatus.TtlExpired, ms, IPAddress.Parse(from));
    private static NetworkTools.Probe Lost => new(IPStatus.TimedOut, 0, null);
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    private static Task<string?> Names(IPAddress a, CancellationToken _) => Task.FromResult<string?>(a.ToString() switch
    {
        "192.168.1.1" => "router.lan", "203.0.113.9" => "ae-5.provider.example", "8.8.8.8" => "dns.google", _ => null,
    });

    // ---------- trace ----------

    [Fact]
    public async Task A_trace_comes_back_as_hops_with_names_and_says_where_the_time_jumps()
    {
        var fake = new Fake((_, ttl, _) => ttl switch { 1 => Hop("192.168.1.1", 1), 2 => Hop("10.20.0.1", 9), 3 => Hop("203.0.113.9", 45), _ => Ok(46) });
        var t = await new NetworkTools(fake, null, Names).TraceHops(Ip("8.8.8.8"), default);
        Assert.True(t.Reached);
        Assert.Equal(4, t.Hops.Count);
        Assert.Equal(("192.168.1.1", "router.lan", 1), (t.Hops[0].Address, t.Hops[0].Name, t.Hops[0].Ms));
        Assert.Null(t.Hops[1].Name);
        Assert.True(t.Hops[3].Final);
        Assert.Equal("Reached 8.8.8.8 in 4 hops", t.Summary);
        Assert.Contains("jumps from 9 ms to 45 ms at hop 3", t.Finding);
        Assert.Contains("first step past your own network", t.Finding);
    }

    [Fact]
    public async Task A_silent_hop_is_a_gap_and_five_in_a_row_stop_the_trace()
    {
        var fake = new Fake((_, ttl, _) => ttl == 1 ? Hop("192.168.1.1", 1) : Lost);
        var t = await new NetworkTools(fake, null, Names).TraceHops(Ip("203.0.113.50"), default);
        Assert.False(t.Reached);
        Assert.Equal(1 + NetworkTools.StopAfterSilentHops, t.Hops.Count);
        Assert.All(t.Hops.Skip(1), h => Assert.Null(h.Address));
        Assert.StartsWith("Didn't reach", t.Summary);
        Assert.Null(t.Finding);                                       // one answering hop is nothing to compare
    }

    [Fact]
    public void The_jump_is_put_in_its_place_inside_the_network_at_the_provider_or_beyond()
    {
        NetworkTools.TraceHop H(int n, string a, int ms) => new(n, a, null, ms, false);
        Assert.Contains("still inside your own network", NetworkTools.TraceFinding(new[] { H(1, "192.168.1.1", 1), H(2, "192.168.1.2", 40) }));
        Assert.Contains("beyond your provider", NetworkTools.TraceFinding(new[] { H(1, "192.168.1.1", 1), H(2, "203.0.113.1", 6), H(3, "198.51.100.1", 80) }));
        Assert.Null(NetworkTools.TraceFinding(new[] { H(1, "192.168.1.1", 1), H(2, "203.0.113.1", 20) }));    // under 25 ms is just distance
    }

    // ---------- path ping ----------

    [Fact]
    public async Task Path_ping_gives_per_hop_loss_and_tells_real_loss_from_a_router_that_ignores_pings()
    {
        // hop 2's address loses every other ping; the rest answer; hop 3 is silent to pings (answers the trace only).
        var seen = new Dictionary<string, int>();
        var fake = new Fake((a, ttl, _) =>
        {
            if (ttl is > 0 and < 128)
                return ttl switch { 1 => Hop("192.168.1.1", 1), 2 => Hop("203.0.113.9", 12), 3 => Hop("198.51.100.4", 15), _ => Ok(16) };
            int k; lock (seen) { seen[a.ToString()] = k = seen.GetValueOrDefault(a.ToString()) + 1; }
            return a.ToString() switch { "203.0.113.9" => k % 2 == 0 ? Lost : Ok(12), "198.51.100.4" => Lost, _ => Ok(2) };
        });
        var p = await new NetworkTools(fake, null, Names).PathHops(Ip("8.8.8.8"), default);
        Assert.Equal(4, p.Hops.Count);
        Assert.Equal(0, p.Hops[0].LossPercent);
        Assert.Equal(50, p.Hops[1].LossPercent);
        Assert.True(p.Hops[2].Silent);
        Assert.Equal(0, p.Hops[3].LossPercent);
        Assert.Contains("loses 50%", p.Finding);
        Assert.Contains("ignores pings", p.Finding);
    }

    [Fact]
    public void Loss_that_carries_on_to_the_end_is_real()
    {
        NetworkTools.PathHop H(int n, int loss) => new(n, $"203.0.113.{n}", null, false, loss, 5, 6, 7);
        Assert.Contains("carries on to the end", NetworkTools.PathFinding(new[] { H(1, 0), H(2, 25), H(3, 50), H(4, 25) }));
        Assert.Equal("No hop lost a ping.", NetworkTools.PathFinding(new[] { H(1, 0), H(2, 0) }));
    }

    // ---------- DNS ----------

    private static NetworkTools DnsTools(Func<string, DnsClient.Answer> answer)
    {
        var t = new NetworkTools(new Fake((_, _, _) => Ok(1)));
        t.DnsServers = () => new List<IPAddress> { Ip("192.168.1.1") };
        t.DnsQuery = (server, name, _) => Task.FromResult(answer(server.ToString()));
        return t;
    }

    private static DnsClient.Answer Found(int ms, params string[] ips) => new(ips.Select(IPAddress.Parse).ToList(), ms, 0, null);
    private static DnsClient.Answer NoSuch => new(new(), 8, 3, null);

    [Fact]
    public async Task The_same_name_is_asked_of_three_resolvers_and_they_agree()
    {
        var d = await DnsTools(_ => Found(11, "93.184.216.34")).DnsCompare("example.com", true, default);
        Assert.Equal(new[] { "Your DNS server", "Cloudflare", "Google" }, d.Answers.Select(a => a.Label));
        Assert.True(d.Agree);
        Assert.Equal("All 3 resolvers agree on the same answer.", d.Summary);
    }

    [Fact]
    public async Task One_resolver_finding_a_name_the_others_say_does_not_exist_is_called_out()
    {
        var d = await DnsTools(s => s == "192.168.1.1" ? Found(11, "10.9.9.9") : NoSuch).DnsCompare("example.com", true, default);
        Assert.False(d.Agree);
        Assert.Contains("disagree about whether example.com exists", d.Summary);
        Assert.Contains("Your DNS server found it", d.Summary);
    }

    [Fact]
    public async Task Different_addresses_are_not_proof_of_anything_and_a_device_name_stays_in_the_house()
    {
        var d = await DnsTools(s => s == "8.8.8.8" ? Found(9, "1.1.1.2") : Found(9, "1.1.1.1")).DnsCompare("cdn.example", true, default);
        Assert.False(d.Agree);
        Assert.Contains("normal for big sites", d.Summary);
        var local = await DnsTools(_ => Found(9, "192.168.30.10")).DnsCompare("nas", false, default);
        Assert.Equal(new[] { "Your DNS server" }, local.Answers.Select(a => a.Label));       // not sent to anyone outside
    }

    // ---------- HTTP ----------

    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("example.com:80", "http://example.com/")]
    [InlineData("http://192.168.1.1/status?x=1", "http://192.168.1.1/status?x=1")]
    [InlineData("https://example.com:8443/health", "https://example.com:8443/health")]
    public void These_web_addresses_are_accepted(string input, string expected)
    {
        Assert.Null(NetworkTools.ParseHttpTarget(input, out var uri));
        Assert.Equal(expected, uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://example.com")]
    [InlineData("https://user:pw@example.com")]
    [InlineData("https://exa mple.com")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("https://example.com/\u0007")]
    public void These_are_refused(string input) => Assert.NotNull(NetworkTools.ParseHttpTarget(input, out _));

    private static (TcpListener Listener, Task Server) WebServer(Func<string, string> respond, X509Certificate2? cert = null)
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var task = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using var _c = c;
                    Stream s = c.GetStream();
                    try
                    {
                        if (cert is not null) { var ssl = new SslStream(s); await ssl.AuthenticateAsServerAsync(cert); s = ssl; }
                        var buf = new byte[4096]; var n = await s.ReadAsync(buf);
                        var path = Encoding.ASCII.GetString(buf, 0, n).Split(' ')[1];
                        await s.WriteAsync(Encoding.ASCII.GetBytes(respond(path)));
                        await s.FlushAsync();
                    }
                    catch { }
                });
            }
        });
        return (l, task);
    }

    [Fact]
    public async Task An_http_check_follows_a_redirect_and_times_each_step()
    {
        var (l, _) = WebServer(path => path == "/" ? "HTTP/1.1 301 Moved Permanently\r\nLocation: /final\r\nServer: test\r\nContent-Length: 0\r\n\r\n"
                                                    : "HTTP/1.1 200 OK\r\nServer: test\r\nContent-Length: 2\r\n\r\nok");
        try
        {
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            var d = await new NetworkTools().HttpCheck($"http://127.0.0.1:{port}/", default, allowLoopback: true);
            Assert.True(d.Ok);
            Assert.Equal(new int?[] { 301, 200 }, d.Steps.Select(s => s.Status));
            Assert.EndsWith("/final", d.Steps[1].Url);
            Assert.All(d.Steps, s => { Assert.NotNull(s.ConnectMs); Assert.NotNull(s.FirstByteMs); Assert.Null(s.TlsMs); Assert.Equal("test", s.Server); });
            Assert.Null(d.Cert);
            Assert.Contains("200 OK", d.Summary);
            Assert.Contains("after 1 redirect", d.Summary);
        }
        finally { l.Stop(); }
    }

    [Fact]
    public async Task An_http_check_says_what_is_wrong_when_nothing_listens_or_it_is_not_a_web_server()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0); closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
        var refused = await new NetworkTools().HttpCheck($"http://127.0.0.1:{port}/", default, allowLoopback: true);
        Assert.False(refused.Ok);
        Assert.Contains($"Nothing is listening on port {port}", refused.Summary);

        var (l, _) = WebServer(_ => "SSH-2.0-OpenSSH\r\n");
        try
        {
            var d = await new NetworkTools().HttpCheck($"http://127.0.0.1:{((IPEndPoint)l.LocalEndpoint).Port}/", default, allowLoopback: true);
            Assert.False(d.Ok);
            Assert.Contains("not with a web page", d.Summary);
        }
        finally { l.Stop(); }
    }

    [Fact]
    public async Task An_https_check_reads_the_certificate_and_says_it_is_not_trusted_when_it_is_self_signed()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=lab.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(45));
        using var cert = new X509Certificate2(made.Export(X509ContentType.Pfx));
        var (l, _) = WebServer(_ => "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n", cert);
        try
        {
            var d = await new NetworkTools().HttpCheck($"https://127.0.0.1:{((IPEndPoint)l.LocalEndpoint).Port}/", default, allowLoopback: true);
            Assert.True(d.Ok);
            Assert.NotNull(d.Steps[0].TlsMs);
            Assert.NotNull(d.Cert);
            Assert.Equal("lab.test", d.Cert!.Subject);
            Assert.InRange(d.Cert.DaysLeft, 43, 45);
            Assert.False(d.Cert.Trusted);
            Assert.NotNull(d.Cert.Problem);
            Assert.Contains("certificate not trusted", d.Summary);
        }
        finally { l.Stop(); }
    }

    // ---------- ports ----------

    [Fact]
    public void Ports_are_the_common_set_unless_some_were_typed()
    {
        Assert.Null(NetworkTools.ParsePorts("", out var common));
        Assert.Equal(PortChecker.CommonPorts.Count, common.Count);
        Assert.Null(NetworkTools.ParsePorts("22, 80, 8000-8002", out var some));
        Assert.Equal(new[] { 22, 80, 8000, 8001, 8002 }, some);
        Assert.NotNull(NetworkTools.ParsePorts("nonsense", out _));
        Assert.NotNull(NetworkTools.ParsePorts("1-500", out _));          // more than 200
    }

    [Fact]
    public async Task A_port_check_says_which_ports_answered()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        try
        {
            var open = ((IPEndPoint)l.LocalEndpoint).Port;
            var closed = new TcpListener(IPAddress.Loopback, 0); closed.Start();
            var shut = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
            var r = await new NetworkTools().PortCheck(IPAddress.Loopback, new[] { open, shut }, default);
            Assert.Equal(new[] { open }, r.Open.Select(p => p.Port));
            Assert.Equal(2, r.Tested.Count);
            Assert.Contains("1 of 2 ports open", r.Summary);
        }
        finally { l.Stop(); }
    }

    // ---------- why is it slow ----------

    private static NetworkTools Diag(Func<IPAddress, NetworkTools.Probe> ping, Func<string, DnsClient.Answer> dns, bool resolves = true)
    {
        var t = new NetworkTools(new Fake((a, _, _) => ping(a)), (_, _) => resolves ? Task.FromResult(new[] { Ip("93.184.216.34") }) : throw new SocketException(11001));
        t.Gateways = () => new[] { "192.168.1.1" };
        t.DnsServers = () => new List<IPAddress> { Ip("192.168.1.1") };
        t.DnsQuery = (s, _, _) => Task.FromResult(dns(s.ToString()));
        return t;
    }

    [Fact]
    public async Task A_healthy_network_says_so()
    {
        var d = await Diag(a => Ok(a.ToString() == "192.168.1.1" ? 2 : 14), _ => Found(11, "8.8.8.8")).Diagnose("8.8.8.8", default);
        Assert.Equal(new[] { "router", "dns", "internet", "name" }, d.Steps.Select(s => s.Key));
        Assert.All(d.Steps, s => Assert.Equal("ok", s.Status));
        Assert.Equal("ok", d.Level);
        Assert.Contains("fine", d.Verdict);
    }

    [Fact]
    public async Task The_line_being_down_is_found_with_the_router_and_dns_server_fine()
    {
        var d = await Diag(a => a.ToString() == "192.168.1.1" ? Ok(2) : Lost, _ => new DnsClient.Answer(new(), 3000, -1, "No answer in time."), resolves: false).Diagnose("8.8.8.8", default);
        Assert.Equal("bad", d.Level);
        Assert.Contains("nothing beyond it does", d.Verdict);
        Assert.Equal("fail", d.Steps.Single(s => s.Key == "internet").Status);
    }

    [Fact]
    public void Each_kind_of_trouble_gets_its_own_sentence()
    {
        NetworkTools.DiagStep S(string k, string status, int? ms = 5) => new(k, k, "x", status, ms, "");
        (string Level, string Text) J(params NetworkTools.DiagStep[] s) => NetworkTools.Judge(s);
        Assert.Contains("on your side of the wall", J(S("router", "fail"), S("dns", "ok"), S("internet", "ok"), S("name", "ok")).Text);
        Assert.Contains("your DNS server doesn't", J(S("router", "ok"), S("dns", "fail"), S("internet", "ok"), S("name", "fail")).Text);
        Assert.Contains("only pings to", J(S("router", "ok"), S("dns", "ok"), S("internet", "fail"), S("name", "ok")).Text);
        Assert.Equal("warn", J(S("router", "ok"), S("dns", "ok"), S("internet", "fail"), S("name", "ok")).Level);
        Assert.Contains("beyond the router", J(S("router", "ok", 2), S("dns", "ok"), S("internet", "slow", 400), S("name", "ok")).Text);
        Assert.Contains("on your side", J(S("router", "slow", 80), S("dns", "ok"), S("internet", "ok"), S("name", "ok")).Text);
        Assert.Contains("every page waits", J(S("router", "ok"), S("dns", "slow", 900), S("internet", "ok"), S("name", "slow", 900)).Text);
        Assert.Equal("ok", J(S("router", "skipped"), S("dns", "skipped"), S("internet", "ok"), S("name", "ok")).Level);
    }

    // ---------- the endpoints ----------

    private static WebApplicationFactory<Program> With(BamfApp app, NetworkTools tools) => app.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton(tools)));

    [Fact]
    public async Task The_tab_reports_what_it_may_do_and_a_typed_address_needs_the_switch()
    {
        var fake = new Fake((_, ttl, _) => ttl == 1 ? Hop("192.168.1.1", 1) : Ok(6));
        using var app = new BamfApp();
        using var host = With(app, new NetworkTools(fake, (n, _) => Task.FromResult(new[] { Ip("93.184.216.34") }), Names));
        var c = host.CreateClient();
        var caps = await c.GetFromJsonAsync<JsonElement>("/api/tools");
        Assert.True(caps.GetProperty("enabled").GetBoolean());
        Assert.False(caps.GetProperty("anywhere").GetBoolean());
        Assert.Equal(PortChecker.CommonPorts.Count, caps.GetProperty("commonPorts").GetArrayLength());

        var typed = await c.PostAsJsonAsync("/api/tools/run", new { tool = "trace", target = "8.8.8.8" });
        Assert.Equal(HttpStatusCode.Conflict, typed.StatusCode);
        Assert.Contains("switched off", (await typed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Empty(fake.Sent);
        var live = await c.PostAsJsonAsync("/api/tools/ping", new { target = "8.8.8.8" });
        Assert.Equal(HttpStatusCode.Conflict, live.StatusCode);

        await c.PostAsJsonAsync("/api/settings/trace-anywhere", new { enabled = true });
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/tools")).GetProperty("anywhere").GetBoolean());
        var ok = await c.PostAsJsonAsync("/api/tools/run", new { tool = "trace", target = "8.8.8.8" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var d = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("8.8.8.8", d.GetProperty("address").GetString());
        Assert.Equal(2, d.GetProperty("data").GetProperty("hops").GetArrayLength());
        Assert.Equal("router.lan", d.GetProperty("data").GetProperty("hops")[0].GetProperty("name").GetString());

        var bad = await c.PostAsJsonAsync("/api/tools/run", new { tool = "trace", target = "127.0.0.1" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var nothing = await c.PostAsJsonAsync("/api/tools/run", new { tool = "nonsense", target = "8.8.8.8" });
        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);
    }

    [Fact]
    public async Task A_device_BAMF_knows_needs_no_switch_whether_picked_or_typed_by_its_address()
    {
        var fake = new Fake((_, ttl, _) => Ok(4));
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        store.UpsertSeen("AA:BB:CC:00:00:09", "192.168.30.9", "garage-cam", "Acme", "192.168.30.0/24");
        var id = store.GetAll().Single(h => h.Mac == "AA:BB:CC:00:00:09").Id;
        using var host = With(app, new NetworkTools(fake));
        var c = host.CreateClient();

        var picked = await c.PostAsJsonAsync("/api/tools/ping", new { hostId = id });
        Assert.Equal(HttpStatusCode.OK, picked.StatusCode);
        var p = await picked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(p.GetProperty("ok").GetBoolean());
        Assert.Equal(4, p.GetProperty("ms").GetInt64());
        Assert.Equal("192.168.30.9", p.GetProperty("address").GetString());

        var typed = await c.PostAsJsonAsync("/api/tools/ping", new { target = "192.168.30.9" });      // a device's address typed in
        Assert.Equal(HttpStatusCode.OK, typed.StatusCode);
        var trace = await c.PostAsJsonAsync("/api/tools/run", new { tool = "trace", hostId = id });
        Assert.Equal(HttpStatusCode.OK, trace.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/tools/ping", new { hostId = 99999 })).StatusCode);

        await c.PostAsJsonAsync("/api/settings/network-tools", new { enabled = false });
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/tools/ping", new { hostId = id })).StatusCode);
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/tools")).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task The_http_and_port_checks_of_a_known_device_work_and_of_a_stranger_wait_for_the_switch()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        store.UpsertSeen("AA:BB:CC:00:00:09", "192.168.30.9", "garage-cam", "Acme", "192.168.30.0/24");
        var id = store.GetAll().Single(h => h.Mac == "AA:BB:CC:00:00:09").Id;
        var c = app.Client();

        var stranger = await c.PostAsJsonAsync("/api/tools/run", new { tool = "http", target = "https://example.com" });
        Assert.Equal(HttpStatusCode.Conflict, stranger.StatusCode);
        var strangerPorts = await c.PostAsJsonAsync("/api/tools/run", new { tool = "port", target = "203.0.113.5" });
        Assert.Equal(HttpStatusCode.Conflict, strangerPorts.StatusCode);
        var badSpec = await c.PostAsJsonAsync("/api/tools/run", new { tool = "port", hostId = id, ports = "nonsense" });
        Assert.Equal(HttpStatusCode.BadRequest, badSpec.StatusCode);
        var badUrl = await c.PostAsJsonAsync("/api/tools/run", new { tool = "http", target = "ftp://x" });
        Assert.Equal(HttpStatusCode.BadRequest, badUrl.StatusCode);
    }

    [Fact]
    public async Task The_diagnosis_endpoint_answers_with_its_four_steps()
    {
        using var app = new BamfApp();
        var t = Diag(a => Ok(a.ToString() == "192.168.1.1" ? 2 : 14), _ => Found(11, "8.8.8.8"));
        using var host = With(app, t);
        var c = host.CreateClient();
        var d = await (await c.PostAsync("/api/tools/diagnose", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", d.GetProperty("level").GetString());
        Assert.Equal(4, d.GetProperty("steps").GetArrayLength());
        Assert.Equal("router", d.GetProperty("steps")[0].GetProperty("key").GetString());
    }
}
