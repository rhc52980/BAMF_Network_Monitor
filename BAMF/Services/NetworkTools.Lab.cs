using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LanWatch.Services;

/// <summary>
/// The Tools tab's side of the network tools: the same probes as the text tools beside it, but answering with structured results a page can
/// draw (hops with names and times, per-hop loss, the answers three resolvers gave, how long each step of a web request took, which ports
/// answered), plus the two new checks (HTTP and port) and the "why is it slow?" diagnosis. Nothing here runs a program or puts what was
/// typed into a command: a probe is an ICMP echo or a TCP connection to an address that has been checked.
/// </summary>
public sealed partial class NetworkTools
{
    // ------------------------------------------------------------ results

    public sealed record TraceHop(int N, string? Address, string? Name, int? Ms, bool Final);
    public sealed record TraceData(string Address, bool Reached, IReadOnlyList<TraceHop> Hops, string Summary, string? Finding);

    public sealed record PathHop(int N, string? Address, string? Name, bool Silent, int LossPercent, int? Min, int? Avg, int? Max);
    public sealed record PathData(string Address, bool Reached, IReadOnlyList<PathHop> Hops, string Summary, string? Finding);

    public sealed record DnsRow(string Label, string Server, int? Ms, IReadOnlyList<string> Addresses, string Status, string? Error);
    public sealed record DnsData(string Name, IReadOnlyList<DnsRow> Answers, bool Agree, string Summary);
    public sealed record ReverseData(string Address, string? Name, string Summary);

    public sealed record HttpStep(string Url, int? Status, string? Reason, int? DnsMs, int? ConnectMs, int? TlsMs, int? FirstByteMs, int TotalMs, string? Location, string? Server, string? Error);
    public sealed record CertInfo(string? Subject, string? Issuer, string NotAfter, int DaysLeft, bool Trusted, string? Problem);
    public sealed record HttpData(string Url, bool Ok, IReadOnlyList<HttpStep> Steps, CertInfo? Cert, string Summary);

    public sealed record OpenPort(int Port, string Service);
    public sealed record PortData(string Address, IReadOnlyList<int> Tested, IReadOnlyList<OpenPort> Open, int Ms, string Summary);

    public sealed record DiagStep(string Key, string Label, string Target, string Status, int? Ms, string Detail);
    public sealed record DiagData(IReadOnlyList<DiagStep> Steps, string Level, string Verdict);

    // ------------------------------------------------------------ guards

    private readonly SemaphoreSlim _probeSlots = new(6);

    /// <summary>
    /// Runs one tool at a time. A target that was typed (not a device BAMF knows) also waits <see cref="AnywhereGap"/> since the last one.
    /// Null when another tool is running; <see cref="InvalidOperationException"/> when it was too soon.
    /// </summary>
    public async Task<T?> Exclusive<T>(bool typed, CancellationToken ct, Func<CancellationToken, Task<T>> work, int seconds = 45, DateTime? nowUtc = null) where T : class
    {
        if (!await _one.WaitAsync(0, ct)) return null;
        try
        {
            var now = nowUtc ?? DateTime.UtcNow;
            if (typed)
            {
                if (now - _lastAnywhere < AnywhereGap)
                    throw new InvalidOperationException($"One at a time, with a short wait between. Try again in {Math.Max(1, (int)Math.Ceiling((AnywhereGap - (now - _lastAnywhere)).TotalSeconds))} seconds.");
                _lastAnywhere = now;
            }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(seconds));
            return await work(limit.Token);
        }
        finally { if (typed && nowUtc is null) _lastAnywhere = DateTime.UtcNow; _one.Release(); }
    }

    /// <summary>
    /// Checks a typed target and finds the IPv4 address it means: a name is looked up and its first IPv4 address used. The reason, for the
    /// person, as an <see cref="ArgumentException"/>.
    /// </summary>
    public async Task<(IPAddress Ip, string? Note)> ResolveTyped(string? input, CancellationToken ct)
    {
        var bad = CheckTarget(input);
        if (bad is not null) throw new ArgumentException(bad);
        var t = input!.Trim();
        if (IPAddress.TryParse(t, out var ip)) return (ip, null);
        IPAddress[] found;
        try { found = await _resolve(t, ct); }
        catch (SocketException) { throw new ArgumentException($"{t} doesn't resolve to an address."); }
        var v4 = found.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
        if (v4.Count == 0) throw new ArgumentException($"{t} has no IPv4 address.");
        ip = v4[0];
        if (CheckAddress(ip) is { } why) throw new ArgumentException($"{t} points at {ip}: {why}");
        return (ip, $"{t} is {ip}" + (v4.Count > 1 ? $" (it has {v4.Count} addresses; the first is used)" : ""));
    }

    // ------------------------------------------------------------ live ping

    /// <summary>One echo, for the page's live ping: it asks once a second. Null when too many are going at once.</summary>
    public async Task<Probe?> PingOnce(IPAddress ip, CancellationToken ct)
    {
        if (!await _probeSlots.WaitAsync(0, ct)) return null;
        try { return await Safe(ip, PingTimeoutMs, 128, ct); }
        finally { _probeSlots.Release(); }
    }

    // ------------------------------------------------------------ names

    private async Task<string?> ReverseOf(IPAddress ip, CancellationToken ct)
    {
        if (_reverse is not null) return await _reverse(ip, ct);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMilliseconds(1200));
            var e = await System.Net.Dns.GetHostEntryAsync(ip).WaitAsync(limit.Token);
            return string.IsNullOrEmpty(e.HostName) || e.HostName == ip.ToString() ? null : e.HostName;
        }
        catch { return null; }
    }

    internal static bool IsPrivate(string? address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    // ------------------------------------------------------------ trace

    public async Task<TraceData> TraceHops(IPAddress target, CancellationToken ct)
    {
        var raw = new List<(int N, IPAddress? Addr, int? Ms, bool Final)>();
        var silent = 0; var reached = false;
        for (var ttl = 1; ttl <= MaxHops && !reached; ttl++)
        {
            ct.ThrowIfCancellationRequested();
            var p = await Safe(target, HopTimeoutMs, ttl, ct);
            if (p.Status == IPStatus.Success) { raw.Add((ttl, target, (int)p.Ms, true)); reached = true; silent = 0; }
            else if (p.Status is IPStatus.TtlExpired or IPStatus.TimeExceeded && p.From is not null) { raw.Add((ttl, p.From, (int)p.Ms, false)); silent = 0; }
            else { raw.Add((ttl, null, null, false)); silent++; }
            if (silent >= StopAfterSilentHops) break;
        }
        var names = await Task.WhenAll(raw.Select(r => r.Addr is null ? Task.FromResult<string?>(null) : ReverseOf(r.Addr, ct)));
        var hops = raw.Select((r, i) => new TraceHop(r.N, r.Addr?.ToString(), names[i], r.Ms, r.Final)).ToList();
        var counted = hops.Count(h => h.Address is not null);
        var summary = reached ? $"Reached {target} in {hops.Count} hop{(hops.Count == 1 ? "" : "s")}" : $"Didn't reach {target}: the last {silent} hops didn't answer";
        return new TraceData(target.ToString(), reached, hops, summary, counted > 1 ? TraceFinding(hops) : null);
    }

    /// <summary>
    /// Where the time jumps, in words: the biggest rise from one answering hop to the next, if it is at least 25 ms, and what lies on
    /// either side of it (still inside the network, the first hop past the router, or further out).
    /// </summary>
    public static string? TraceFinding(IReadOnlyList<TraceHop> hops)
    {
        TraceHop? prev = null, at = null; var best = 0;
        foreach (var h in hops.Where(h => h.Ms is not null))
        {
            if (prev is not null && h.Ms - prev.Ms is { } jump && jump > best) { best = jump; at = h; }
            prev = h;
        }
        if (at is null || best < 25) return null;
        var before = hops.Where(h => h.Ms is not null && h.N < at.N).Last();
        var where = at.Address is null ? "" : $" ({at.Address})";
        if (IsPrivate(at.Address)) return $"The time jumps from {before.Ms} ms to {at.Ms} ms at hop {at.N}{where}, still inside your own network: something between here and there is slow.";
        if (IsPrivate(before.Address)) return $"The time jumps from {before.Ms} ms to {at.Ms} ms at hop {at.N}{where}, the first step past your own network: that is your provider or the line to them.";
        return $"The time jumps from {before.Ms} ms to {at.Ms} ms at hop {at.N}{where}, out on the internet beyond your provider: not something that can be fixed from here.";
    }

    // ------------------------------------------------------------ path ping

    public async Task<PathData> PathHops(IPAddress target, CancellationToken ct)
    {
        var trace = await TraceHops(target, ct);
        var rows = await Task.WhenAll(trace.Hops.Select(async h =>
        {
            if (h.Address is null || !IPAddress.TryParse(h.Address, out var addr)) return new PathHop(h.N, h.Address, h.Name, true, 0, null, null, null);
            var times = new List<long>();
            for (var i = 0; i < PathPings; i++)
            {
                ct.ThrowIfCancellationRequested();
                var p = await Safe(addr, PathTimeoutMs, 128, ct);
                if (p.Status == IPStatus.Success) times.Add(p.Ms);
                if (i < PathPings - 1) await Task.Delay(150, ct);
            }
            return times.Count == 0
                ? new PathHop(h.N, h.Address, h.Name, true, 0, null, null, null)
                : new PathHop(h.N, h.Address, h.Name, false, (PathPings - times.Count) * 100 / PathPings, (int)times.Min(), (int)Math.Round(times.Average()), (int)times.Max());
        }));
        var answered = rows.Where(r => r.Address is not null).ToList();
        var summary = answered.Count == 0 ? $"No router answered the trace to {target}, so there is no hop to ping" : answered.All(r => r.Silent) || answered.Count(r => !r.Silent) == 0
            ? $"{answered.Count} hops answered the trace but none answers a ping" : $"{answered.Count(r => !r.Silent)} hops pinged";
        return new PathData(target.ToString(), trace.Reached, rows, summary, PathFinding(rows));
    }

    /// <summary>Loss that starts at one hop and carries on to the end is real; loss at only one hop is usually a router that ignores pings.</summary>
    public static string? PathFinding(IReadOnlyList<PathHop> hops)
    {
        var pinged = hops.Where(h => !h.Silent).ToList();
        var first = pinged.FirstOrDefault(h => h.LossPercent >= 10);
        if (first is null) return pinged.Count == 0 ? null : "No hop lost a ping.";
        var after = pinged.Where(h => h.N > first.N).ToList();
        var where = first.Address is null ? "" : $" ({first.Address})";
        return after.Count > 0 && after.All(h => h.LossPercent >= 10)
            ? $"Loss starts at hop {first.N}{where} ({first.LossPercent}%) and carries on to the end: real loss, from there on."
            : $"Hop {first.N}{where} loses {first.LossPercent}% but the hops after it don't: usually a router that answers a trace but ignores pings, not real loss.";
    }

    // ------------------------------------------------------------ DNS

    internal Func<IPAddress, string, CancellationToken, Task<DnsClient.Answer>> DnsQuery { get; set; } = (s, n, ct) => DnsClient.QueryA(s, n, 3000, ct);
    internal Func<List<IPAddress>> DnsServers { get; set; } = DnsClient.SystemServers;

    /// <summary>
    /// The same name asked of this network's own DNS server and, when asked to, of Cloudflare and Google. Big sites hand different addresses to
    /// different places, so different answers aren't proof of anything; one resolver finding the name while the others say there is none is.
    /// </summary>
    public async Task<DnsData> DnsCompare(string name, bool includePublic, CancellationToken ct)
    {
        var servers = new List<(string Label, IPAddress Ip)>();
        foreach (var s in DnsServers().Take(2)) servers.Add(("Your DNS server", s));
        if (includePublic) { servers.Add(("Cloudflare", IPAddress.Parse("1.1.1.1"))); servers.Add(("Google", IPAddress.Parse("8.8.8.8"))); }
        var rows = await Task.WhenAll(servers.Select(async s =>
        {
            var a = await DnsQuery(s.Ip, name, ct);
            var addresses = a.Addresses.Select(x => x.ToString()).ToList();
            var status = !a.Ok ? "fail" : a.RCode == 3 ? "nxdomain" : a.RCode != 0 ? "fail" : addresses.Count > 0 ? "ok" : "empty";
            return new DnsRow(s.Label, s.Ip.ToString(), a.Ok ? a.Ms : null, addresses, status, a.Ok ? (a.RCode is 0 or 3 ? null : $"The server answered with error {a.RCode}.") : a.Error);
        }));
        var found = rows.Where(r => r.Status == "ok").ToList();
        var none = rows.Where(r => r.Status == "nxdomain").ToList();
        bool agree = found.Count > 0 && none.Count == 0 && found.Select(r => string.Join(",", r.Addresses.OrderBy(x => x))).Distinct().Count() == 1 && rows.All(r => r.Status == "ok");
        string summary;
        if (rows.Length == 0) summary = "This machine has no DNS server set, so there is nothing to ask.";
        else if (found.Count == 0 && none.Count > 0) summary = $"No such name: {none.Count} of {rows.Length} resolvers say {name} doesn't exist.";
        else if (found.Count == 0) summary = "None of the resolvers answered.";
        else if (none.Count > 0) summary = $"The resolvers disagree about whether {name} exists: {string.Join(", ", found.Select(r => r.Label))} found it, {string.Join(", ", none.Select(r => r.Label))} didn't. The one that's different is the one to check.";
        else if (agree) summary = $"All {rows.Length} resolver{(rows.Length == 1 ? "" : "s")} {(rows.Length == 1 ? "gives" : "agree on")} the same answer.";
        else if (found.Count < rows.Length) summary = $"{found.Count} of {rows.Length} resolvers answered with an address; the rest didn't answer.";
        else summary = "The resolvers gave different addresses. That's normal for big sites, which hand out different servers to different places.";
        return new DnsData(name, rows, agree, summary);
    }

    public async Task<ReverseData> ReverseLookup(IPAddress ip, CancellationToken ct)
    {
        var name = await ReverseOf(ip, ct);
        return new ReverseData(ip.ToString(), name, name is null ? $"{ip} has no name in DNS (no reverse record)." : $"{ip} is named {name} in DNS.");
    }

    // ------------------------------------------------------------ HTTP

    /// <summary>
    /// Reads what can be typed for an HTTP check: a URL, or just a host (https is assumed, or http for port 80). No login in the address, a name
    /// of letters, digits, dots and dashes or an IPv4 address, a port, and a path and query without spaces or control characters.
    /// </summary>
    public static string? ParseHttpTarget(string? input, out Uri uri, bool allowLoopback = false)
    {
        uri = null!;
        var t = (input ?? "").Trim();
        if (t.Length == 0) return "Type a web address, like example.com or https://example.com/health.";
        if (t.Length > 600) return "That address is too long.";
        if (t.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return "A web address can't have spaces in it.";
        if (!t.Contains("://")) t = (t.EndsWith(":80") ? "http://" : "https://") + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return "That isn't an http:// or https:// address.";
        if (u.UserInfo.Length > 0) return "Leave the user name and password out of the address.";
        var host = u.IdnHost;
        var bad = CheckTarget(host);
        if (bad is not null && !(allowLoopback && IPAddress.TryParse(host, out var lo) && IPAddress.IsLoopback(lo))) return bad;
        uri = u;
        return null;
    }

    private static string? Header(IEnumerable<string> lines, string name) =>
        lines.Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => p[1].Trim()).FirstOrDefault();

    public async Task<HttpData> HttpCheck(string? input, CancellationToken ct, bool allowLoopback = false)
    {
        var bad = ParseHttpTarget(input, out var uri, allowLoopback);
        if (bad is not null) throw new ArgumentException(bad);
        var steps = new List<HttpStep>();
        CertInfo? cert = null;
        var seen = new HashSet<string>();
        for (var follow = 0; follow < 4; follow++)
        {
            if (!seen.Add(uri.AbsoluteUri)) break;
            var (step, c) = await HttpOnce(uri, ct, allowLoopback);
            steps.Add(step);
            cert = c ?? cert;
            if (step.Error is not null || step.Status is not (301 or 302 or 303 or 307 or 308) || string.IsNullOrEmpty(step.Location)) break;
            if (!Uri.TryCreate(uri, step.Location, out var next)) break;
            var again = ParseHttpTarget(next.AbsoluteUri, out var checkedNext, allowLoopback);
            if (again is not null) { steps[^1] = steps[^1] with { Error = $"It redirects somewhere BAMF won't go: {again}" }; break; }
            uri = checkedNext;
        }
        var last = steps[^1];
        var ok = last.Error is null && last.Status is >= 100 and < 400;
        var redirects = steps.Count - 1;
        var summary = last.Error is not null ? last.Error
            : $"{last.Status} {last.Reason}".Trim() + $" in {steps.Sum(s => s.TotalMs)} ms" + (redirects > 0 ? $" after {redirects} redirect{(redirects == 1 ? "" : "s")}" : "")
              + (cert is not null ? $"; certificate {(cert.Trusted ? "valid" : "not trusted")}, {(cert.DaysLeft >= 0 ? $"{cert.DaysLeft} days left" : $"expired {-cert.DaysLeft} days ago")}" : "");
        return new HttpData(steps[0].Url, ok, steps, cert, summary);
    }

    private async Task<(HttpStep Step, CertInfo? Cert)> HttpOnce(Uri uri, CancellationToken ct, bool allowLoopback)
    {
        var total = Stopwatch.StartNew();
        int? dnsMs = null, connectMs = null, tlsMs = null, firstByteMs = null;
        CertInfo? cert = null;
        HttpStep Fail(string error) => new(uri.AbsoluteUri, null, null, dnsMs, connectMs, tlsMs, firstByteMs, (int)total.ElapsedMilliseconds, null, null, error);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(12));
            var host = uri.IdnHost;
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip!))
            {
                var sw = Stopwatch.StartNew();
                IPAddress[] found;
                try { found = await _resolve(host, limit.Token); }
                catch (SocketException) { return (Fail($"{host} doesn't resolve to an address."), null); }
                dnsMs = (int)sw.ElapsedMilliseconds;
                var v4 = found.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (v4 is null) return (Fail($"{host} has no IPv4 address."), null);
                if (CheckAddress(v4) is { } why && !(allowLoopback && IPAddress.IsLoopback(v4))) return (Fail($"{host} points at {v4}: {why}"), null);
                ip = v4;
            }
            using var tcp = new TcpClient();
            var cw = Stopwatch.StartNew();
            try { await tcp.ConnectAsync(ip, uri.Port, limit.Token); }
            catch (SocketException ex) { return (Fail(ex.SocketErrorCode == SocketError.ConnectionRefused ? $"Nothing is listening on port {uri.Port} at {ip}." : $"Couldn't connect to {ip} port {uri.Port}: {ex.Message}"), null); }
            connectMs = (int)cw.ElapsedMilliseconds;
            Stream stream = tcp.GetStream();
            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                SslPolicyErrors errors = SslPolicyErrors.None;
                X509Certificate2? seenCert = null;
                var ssl = new SslStream(stream, false, (_, c, _, e) => { if (c is not null) seenCert = new X509Certificate2(c); errors = e; return true; });
                var tw = Stopwatch.StartNew();
                try { await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, limit.Token); }
                catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException or IOException)
                { return (Fail($"The secure connection failed: {ex.GetBaseException().Message}"), null); }
                tlsMs = (int)tw.ElapsedMilliseconds;
                stream = ssl;
                if (seenCert is not null)
                {
                    var days = (int)Math.Floor((seenCert.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays);
                    cert = new CertInfo(CommonName(seenCert.Subject), CommonName(seenCert.Issuer), seenCert.NotAfter.ToUniversalTime().ToString("o"), days,
                        errors == SslPolicyErrors.None && days >= 0, errors == SslPolicyErrors.None ? (days < 0 ? "It has expired." : null) : Describe(errors));
                }
            }
            var path = uri.PathAndQuery.Length == 0 ? "/" : uri.PathAndQuery;
            var hostHeader = uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
            var request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {hostHeader}\r\nUser-Agent: BAMF-tools\r\nAccept: */*\r\nConnection: close\r\n\r\n");
            var rw = Stopwatch.StartNew();
            await stream.WriteAsync(request, limit.Token);
            var buf = new byte[16384]; var len = 0; var headerEnd = -1;
            while (headerEnd < 0 && len < buf.Length)
            {
                var n = await stream.ReadAsync(buf.AsMemory(len), limit.Token);
                if (n == 0) break;
                if (firstByteMs is null) firstByteMs = (int)rw.ElapsedMilliseconds;
                len += n;
                headerEnd = Encoding.ASCII.GetString(buf, 0, len).IndexOf("\r\n\r\n", StringComparison.Ordinal);
            }
            var head = Encoding.ASCII.GetString(buf, 0, len);
            if (headerEnd >= 0) head = head[..headerEnd];
            var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var status = lines.Length > 0 ? lines[0].Split(' ', 3) : Array.Empty<string>();
            if (status.Length < 2 || !status[0].StartsWith("HTTP/") || !int.TryParse(status[1], out var code))
                return (Fail("It answered, but not with a web page (no HTTP status came back)."), cert);
            return (new HttpStep(uri.AbsoluteUri, code, status.Length > 2 ? status[2] : "", dnsMs, connectMs, tlsMs, firstByteMs, (int)total.ElapsedMilliseconds,
                Header(lines.Skip(1), "Location"), Header(lines.Skip(1), "Server"), null), cert);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (Fail("It didn't answer in 12 seconds."), cert); }
        catch (Exception ex) when (ex is IOException or SocketException) { return (Fail($"The connection broke: {ex.GetBaseException().Message}"), cert); }
    }

    private static string? CommonName(string dn)
    {
        foreach (var part in dn.Split(','))
        {
            var p = part.Trim();
            if (p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return p[3..].Trim();
        }
        return dn.Length is 0 ? null : dn.Length > 120 ? dn[..120] : dn;
    }

    private static string Describe(SslPolicyErrors e)
    {
        var parts = new List<string>();
        if (e.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) parts.Add("it isn't for this name");
        if (e.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)) parts.Add("it isn't signed by anyone this machine trusts (a self-signed or expired certificate)");
        if (e.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) parts.Add("none was offered");
        return parts.Count == 0 ? e.ToString() : string.Join("; ", parts) + ".";
    }

    // ------------------------------------------------------------ ports

    public const int MaxCheckPorts = 200;

    /// <summary>The ports to check: the common set when nothing was typed, else a list like "22,80,8000-8010" of at most <see cref="MaxCheckPorts"/>.</summary>
    public static string? ParsePorts(string? spec, out List<int> ports)
    {
        ports = PortChecker.CommonPorts.Select(p => p.Port).ToList();
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var parsed = PortChecker.ParseSpec(spec);
        if (parsed is null) return "Type ports like 22,80,443 or 8000-8010.";
        if (parsed.Count > MaxCheckPorts) return $"At most {MaxCheckPorts} ports at a time.";
        ports = parsed;
        return null;
    }

    public async Task<PortData> PortCheck(IPAddress ip, IReadOnlyList<int> ports, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var open = await PortChecker.ScanAsync(ip.ToString(), ports, 1000, ct, 32);
        var rows = open.Select(p => new OpenPort(p.Port, p.Service)).ToList();
        var ms = (int)sw.ElapsedMilliseconds;
        var summary = rows.Count == 0 ? $"None of the {ports.Count} ports answered at {ip}" : $"{rows.Count} of {ports.Count} ports open at {ip}: {string.Join(", ", rows.Take(8).Select(r => r.Service.Length > 0 ? $"{r.Port} {r.Service}" : r.Port.ToString()))}{(rows.Count > 8 ? $" and {rows.Count - 8} more" : "")}";
        return new PortData(ip.ToString(), ports, rows, ms, summary);
    }

    // ------------------------------------------------------------ why is it slow?

    internal Func<IEnumerable<string>> Gateways { get; set; } = () => PortChecker.DefaultGateways();
    public const int DiagPings = 3, DiagPingTimeoutMs = 1500;
    public const int RouterSlowMs = 30, InternetSlowMs = 150, DnsSlowMs = 300;

    private async Task<(int Answered, int? AvgMs)> PingAvg(IPAddress ip, CancellationToken ct)
    {
        var times = new List<long>();
        for (var i = 0; i < DiagPings; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = await Safe(ip, DiagPingTimeoutMs, 128, ct);
            if (p.Status == IPStatus.Success) times.Add(p.Ms);
            if (i < DiagPings - 1) await Task.Delay(120, ct);
        }
        return (times.Count, times.Count == 0 ? null : (int)Math.Round(times.Average()));
    }

    /// <summary>
    /// Your router, your DNS server, the internet and a web name, in that order, and where it breaks. The targets are fixed (this machine's
    /// own gateway and DNS server, the internet watch's address, and example.com), so nothing typed is involved.
    /// </summary>
    public async Task<DiagData> Diagnose(string internetTarget, CancellationToken ct)
    {
        var steps = new List<DiagStep>();

        var gateway = Gateways().Select(g => IPAddress.TryParse(g, out var a) ? a : null).FirstOrDefault(a => a is not null);
        if (gateway is null) steps.Add(new DiagStep("router", "Your router", "", "skipped", null, "BAMF doesn't know your router's address."));
        else
        {
            var (n, avg) = await PingAvg(gateway, ct);
            steps.Add(n == 0 ? new DiagStep("router", "Your router", gateway.ToString(), "fail", null, "no answer")
                : new DiagStep("router", "Your router", gateway.ToString(), avg >= RouterSlowMs ? "slow" : "ok", avg, $"{avg} ms" + (n < DiagPings ? $", lost {DiagPings - n} of {DiagPings}" : "")));
        }

        var dns = DnsServers().FirstOrDefault();
        if (dns is null) steps.Add(new DiagStep("dns", "Your DNS server", "", "skipped", null, "This machine has no DNS server set."));
        else
        {
            var a = await DnsQuery(dns, "dns.google", ct);
            steps.Add(!a.Ok || a.RCode != 0 ? new DiagStep("dns", "Your DNS server", dns.ToString(), "fail", null, a.Ok ? "answered with an error" : "no answer")
                : new DiagStep("dns", "Your DNS server", dns.ToString(), a.Ms >= DnsSlowMs ? "slow" : "ok", a.Ms, $"answers in {a.Ms} ms"));
        }

        if (!IPAddress.TryParse(internetTarget, out var net)) net = IPAddress.Parse("8.8.8.8");
        var (ni, avgi) = await PingAvg(net, ct);
        steps.Add(ni == 0 ? new DiagStep("internet", "The internet", net.ToString(), "fail", null, "no answer")
            : new DiagStep("internet", "The internet", net.ToString(), avgi >= InternetSlowMs ? "slow" : "ok", avgi, $"{avgi} ms" + (ni < DiagPings ? $", lost {DiagPings - ni} of {DiagPings}" : "")));

        {
            var sw = Stopwatch.StartNew();
            try
            {
                var found = await _resolve("example.com", ct);
                var ms = (int)sw.ElapsedMilliseconds;
                steps.Add(found.Length > 0 ? new DiagStep("name", "A web name", "example.com", ms >= DnsSlowMs ? "slow" : "ok", ms, $"resolves in {ms} ms") : new DiagStep("name", "A web name", "example.com", "fail", null, "no address came back"));
            }
            catch (SocketException) { steps.Add(new DiagStep("name", "A web name", "example.com", "fail", null, "doesn't resolve")); }
        }

        var (level, verdict) = Judge(steps);
        return new DiagData(steps, level, verdict);
    }

    /// <summary>What the four checks add up to, in a sentence, and whether it is fine ("ok"), worth a look ("warn") or broken ("bad").</summary>
    public static (string Level, string Text) Judge(IReadOnlyList<DiagStep> steps)
    {
        DiagStep? S(string k) => steps.FirstOrDefault(s => s.Key == k);
        bool Failed(string k) => S(k)?.Status == "fail";
        bool Slow(string k) => S(k)?.Status == "slow";
        var router = S("router"); var dns = S("dns"); var net = S("internet"); var name = S("name");

        if (Failed("router")) return ("bad", "Your router doesn't answer, so the trouble is on your side of the wall: this machine's cable or Wi-Fi, or the router itself.");
        if (Failed("internet") && Failed("dns") && Failed("name")) return ("bad", "Your router answers, but nothing beyond it does: the line out of the house, the modem or your provider.");
        if (Failed("internet") && dns?.Status is "ok" or "slow") return ("warn", "The internet answers DNS but not pings to " + net?.Target + ": probably only pings to that address are blocked, and browsing should work. Try the HTTP check.");
        if (Failed("internet")) return ("bad", "Your router answers but the internet doesn't: the line out of the house, the modem or your provider.");
        if (Failed("dns")) return ("bad", "The internet answers pings but your DNS server doesn't, so web names won't open. Restart the router, or set a public DNS server (1.1.1.1 or 9.9.9.9).");
        if (Failed("name")) return ("warn", "Your DNS server answers, but looking up a web name failed. Try the DNS lookup to see which resolver is wrong.");
        var notes = new List<string>();
        if (Slow("router")) notes.Add($"Your router takes {router!.Ms} ms to answer, which is slow for something in the same house: Wi-Fi, a busy router, or a cable problem. The delay is on your side.");
        if (Slow("internet")) notes.Add($"The internet takes {net!.Ms} ms while your router answers fast, so the delay is beyond the router: the line, the modem or your provider.");
        if (Slow("dns")) notes.Add($"Your DNS server takes {dns!.Ms} ms to answer, and every page waits on it. Try a public DNS server (1.1.1.1 or 9.9.9.9).");
        if (Slow("name") && !Slow("dns")) notes.Add($"Looking up a web name takes {name!.Ms} ms, which every page waits on.");
        if (notes.Count > 0) return ("warn", string.Join(" ", notes));
        return ("ok", "Everything BAMF can test from here is fine. If one device feels slow, it is that device or its Wi-Fi: ping it from its row.");
    }
}
