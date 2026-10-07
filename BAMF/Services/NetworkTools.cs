using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace LanWatch.Services;

/// <summary>
/// Ping, trace route and DNS lookup for a device, run from the machine BAMF is on and shown in the dashboard, for when something is
/// flaky and the question is "can BAMF reach it, and by what path?". The target is always a device BAMF already knows (its address
/// comes from the device, never from what's typed), so this can't be used to probe the internet. In-process only: no program is run
/// and no text from a request is ever put in a command. One at a time, and each is bounded in time.
/// </summary>
public sealed partial class NetworkTools
{
    /// <summary>One echo request's answer: its status, how long it took, and who answered (a router in the middle, for a trace).</summary>
    public sealed record Probe(IPStatus Status, long Ms, IPAddress? From);

    /// <summary>What actually sends an echo with a given hop limit. Replaceable, so the tools can be tested without a network.</summary>
    public interface IProbe { Task<Probe> SendAsync(IPAddress target, int timeoutMs, int ttl, CancellationToken ct); }

    private sealed class SystemProbe : IProbe
    {
        public async Task<Probe> SendAsync(IPAddress target, int timeoutMs, int ttl, CancellationToken ct)
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(timeoutMs), new byte[32], new PingOptions(ttl, true), ct);
            return new Probe(reply.Status, reply.RoundtripTime, reply.Address);
        }
    }

    public const int PingCount = 4, PingTimeoutMs = 2000, MaxHops = 20, HopTimeoutMs = 1500, StopAfterSilentHops = 5;

    private readonly IProbe _probe;
    private readonly SemaphoreSlim _one = new(1, 1);

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPAddress, CancellationToken, Task<string?>>? _reverse;
    private DateTime _lastAnywhere = DateTime.MinValue;

    public NetworkTools(IProbe? probe = null, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, Func<IPAddress, CancellationToken, Task<string?>>? reverse = null)
    {
        _reverse = reverse;
        _probe = probe ?? new SystemProbe();
        _resolve = resolve ?? ((name, ct) => System.Net.Dns.GetHostAddressesAsync(name, ct));
    }

    /// <summary>The least time between two traces to an address that was typed, so it can't be used to hammer anything.</summary>
    public static readonly TimeSpan AnywhereGap = TimeSpan.FromSeconds(8);

    /// <summary>
    /// What can be typed as a place to trace to: a plain IPv4 address, or a host name of letters, digits, dots, dashes and underscores.
    /// Returns a message for the person, or null when it is fine. Private addresses are allowed; an address that is nowhere (0.0.0.0, the
    /// broadcast address, multicast) or this machine itself is not.
    /// </summary>
    public static string? CheckTarget(string? input)
    {
        var t = (input ?? "").Trim();
        if (t.Length == 0) return "Type an address or a name to trace to, like 8.8.8.8 or example.com.";
        if (t.Length > 253) return "That is too long to be an address or a name.";
        if (t.Contains(':')) return "BAMF traces IPv4 addresses and names that have one.";
        if (IPAddress.TryParse(t, out var ip))
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork || t.Count(c => c == '.') != 3) return "That isn't an IPv4 address (four numbers separated by dots).";
            return CheckAddress(ip);
        }
        if (!t.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) return "A name can only have letters, digits, dots and dashes.";
        if (t.StartsWith('.') || t.EndsWith('.') || t.Contains("..") || t.StartsWith('-')) return "That isn't a valid name.";
        return t.All(c => char.IsAsciiDigit(c) || c == '.') ? "That isn't an IPv4 address (four numbers separated by dots)." : null;
    }

    private static string? CheckAddress(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.All(x => x == 0)) return "0.0.0.0 isn't an address anything has.";
        if (b.All(x => x == 255)) return "That's the broadcast address, not a device.";
        if (b[0] >= 224) return "That's a multicast or reserved address, not a device.";
        if (b[0] == 127) return "That's this machine itself. Trace to something else.";
        return null;
    }

    /// <summary>
    /// Traces the route to an address or name that was typed. A name is looked up first and the first IPv4 address it has is used. One tool
    /// at a time (a second is told to wait, as for a device), and at least <see cref="AnywhereGap"/> between two of these. Returns null when
    /// another tool is running; throws <see cref="ArgumentException"/> with a message for the person when the target isn't acceptable and
    /// <see cref="InvalidOperationException"/> when it was too soon.
    /// </summary>
    public async Task<Result?> TraceAnywhere(string? input, CancellationToken ct, DateTime? nowUtc = null) => await RunAnywhere("trace", input, ct, nowUtc);

    /// <summary>The same for ping, trace and path ping: a typed target, checked, looked up if it is a name, one at a time with a gap.</summary>
    public async Task<Result?> RunAnywhere(string tool, string? input, CancellationToken ct, DateTime? nowUtc = null)
    {
        if (tool is not ("ping" or "trace" or "path")) throw new ArgumentException("Ping, trace or path.");
        var bad = CheckTarget(input);
        if (bad is not null) throw new ArgumentException(bad);
        var now = nowUtc ?? DateTime.UtcNow;
        if (!await _one.WaitAsync(0, ct)) return null;
        try
        {
            if (now - _lastAnywhere < AnywhereGap) throw new InvalidOperationException($"One at a time, with a short wait between. Try again in {Math.Max(1, (int)Math.Ceiling((AnywhereGap - (now - _lastAnywhere)).TotalSeconds))} seconds.");
            _lastAnywhere = now;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(tool == "path" ? 100 : 45));
            var t = input!.Trim();
            string note = "";
            if (!IPAddress.TryParse(t, out var ip))
            {
                IPAddress[] found;
                try { found = await _resolve(t, limit.Token); }
                catch (SocketException) { throw new ArgumentException($"{t} doesn't resolve to an address."); }
                var v4 = found.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
                if (v4.Count == 0) throw new ArgumentException($"{t} has no IPv4 address to trace to.");
                ip = v4[0];
                if (CheckAddress(ip) is { } why) throw new ArgumentException($"{t} points at {ip}: {why}");
                note = $"{t} is {ip}" + (v4.Count > 1 ? $" (it has {v4.Count} addresses; the first is used)" : "");
            }
            var r = tool switch { "ping" => await Ping(ip.ToString(), limit.Token), "path" => await PathPing(ip.ToString(), limit.Token), _ => await Trace(ip.ToString(), limit.Token) };
            var lines = note.Length > 0 ? new[] { note }.Concat(r.Lines).ToList() : r.Lines.ToList();
            return r with { Lines = lines, Target = t == ip.ToString() ? t : $"{t} ({ip})" };
        }
        finally { if (nowUtc is null) _lastAnywhere = DateTime.UtcNow; _one.Release(); }
    }

    public sealed record Result(string Tool, string Target, bool Ok, IReadOnlyList<string> Lines, string Summary);

    /// <summary>True when another tool is running; the caller says "busy". Tools wait for no one.</summary>
    public async Task<Result?> RunAsync(string tool, string ip, string? name, CancellationToken ct)
    {
        if (!await _one.WaitAsync(0, ct)) return null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(tool == "path" ? 100 : 45));
            return tool switch
            {
                "ping" => await Ping(ip, limit.Token),
                "trace" => await Trace(ip, limit.Token),
                "path" => await PathPing(ip, limit.Token),
                "dns" => await Dns(ip, name, limit.Token),
                _ => throw new ArgumentException("Unknown tool"),
            };
        }
        finally { _one.Release(); }
    }

    public static readonly string[] Tools = { "ping", "trace", "path", "dns" };

    internal async Task<Result> Ping(string ip, CancellationToken ct)
    {
        var target = IPAddress.Parse(ip);
        var lines = new List<string>();
        var times = new List<long>();
        for (var i = 1; i <= PingCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = await Safe(target, PingTimeoutMs, 128, ct);
            if (p.Status == IPStatus.Success) { times.Add(p.Ms); lines.Add($"{i}: reply from {ip} in {p.Ms} ms"); }
            else lines.Add($"{i}: {Describe(p.Status)}");
            if (i < PingCount) await Task.Delay(300, ct);
        }
        var lost = PingCount - times.Count;
        var summary = times.Count == 0
            ? $"No answer from {ip}: all {PingCount} lost"
            : $"{times.Count} of {PingCount} answered{(lost > 0 ? $", {lost * 100 / PingCount}% lost" : "")}; {times.Min()} / {times.Average().ToString("0", CultureInfo.InvariantCulture)} / {times.Max()} ms (fastest / average / slowest)";
        return new Result("ping", ip, times.Count > 0, lines, summary);
    }

    internal async Task<Result> Trace(string ip, CancellationToken ct)
    {
        var target = IPAddress.Parse(ip);
        var lines = new List<string>();
        var silent = 0; var reached = false;
        for (var ttl = 1; ttl <= MaxHops && !reached; ttl++)
        {
            ct.ThrowIfCancellationRequested();
            var p = await Safe(target, HopTimeoutMs, ttl, ct);
            switch (p.Status)
            {
                case IPStatus.Success:
                    lines.Add($"{ttl,2}  {ip}  {p.Ms} ms  (the device)"); reached = true; silent = 0; break;
                case IPStatus.TtlExpired or IPStatus.TimeExceeded when p.From is not null:
                    lines.Add($"{ttl,2}  {p.From}  {p.Ms} ms"); silent = 0; break;
                case IPStatus.TimedOut:
                    lines.Add($"{ttl,2}  *  (no answer)"); silent++; break;
                default:
                    lines.Add($"{ttl,2}  {Describe(p.Status)}"); silent++; break;
            }
            if (silent >= StopAfterSilentHops) { lines.Add("Stopping: five hops in a row didn't answer."); break; }
        }
        var hops = lines.Count(l => !l.StartsWith("Stopping"));
        var summary = reached ? $"Reached {ip} in {hops} hop{(hops == 1 ? "" : "s")}" : $"Didn't reach {ip}";
        return new Result("trace", ip, reached, lines, summary);
    }

    public const int PathPings = 4, PathTimeoutMs = 1000;

    /// <summary>
    /// A trace, then a ping of every router that answered it, a few times each: how much each hop loses and how long it takes, which shows
    /// where along the path the trouble starts. Like pathping or mtr. A hop that doesn't answer pings is listed as silent, not as lossy, since
    /// many routers answer a trace's probe but not a ping.
    /// </summary>
    internal async Task<Result> PathPing(string ip, CancellationToken ct)
    {
        var trace = await Trace(ip, ct);
        var hops = new List<(int N, IPAddress Addr)>();
        for (var i = 0; i < trace.Lines.Count; i++)
        {
            var parts = trace.Lines[i].Split("  ", StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out var n) && IPAddress.TryParse(parts[1].Trim(), out var a) && a.AddressFamily == AddressFamily.InterNetwork)
                hops.Add((n, a));
        }
        var lines = new List<string> { " #  address            lost   fastest / average / slowest" };
        var worst = (Hop: 0, Loss: 0);
        foreach (var (n, addr) in hops)
        {
            var times = new List<long>();
            for (var i = 0; i < PathPings; i++)
            {
                ct.ThrowIfCancellationRequested();
                var p = await Safe(addr, PathTimeoutMs, 128, ct);
                if (p.Status == IPStatus.Success) times.Add(p.Ms);
                if (i < PathPings - 1) await Task.Delay(150, ct);
            }
            var lost = (PathPings - times.Count) * 100 / PathPings;
            if (times.Count == 0) lines.Add($"{n,2}  {addr,-17}  silent (answers a trace but not a ping)");
            else
            {
                lines.Add($"{n,2}  {addr,-17}  {lost,3}%   {times.Min()} / {times.Average().ToString("0", CultureInfo.InvariantCulture)} / {times.Max()} ms");
                if (lost > worst.Loss) worst = (n, lost);
            }
        }
        if (hops.Count == 0) lines.Add("No router answered the trace, so there is no hop to ping.");
        var summary = hops.Count == 0 ? $"No hops to ping on the way to {ip}"
            : worst.Loss > 0 ? $"{hops.Count} hops pinged; the first loss is at hop {worst.Hop} ({worst.Loss}%). Loss that starts at one hop and carries on to the end is real, and loss at only one hop is usually a router that ignores pings."
            : $"{hops.Count} hops pinged, none lost a ping";
        return new Result("path", ip, trace.Ok, lines, summary);
    }

    internal async Task<Result> Dns(string ip, string? name, CancellationToken ct)
    {
        var lines = new List<string>();
        var ok = false;
        var address = IPAddress.Parse(ip);
        try
        {
            var back = await System.Net.Dns.GetHostEntryAsync(address).WaitAsync(ct);
            lines.Add($"{ip} is named {string.Join(", ", back.HostName is { Length: > 0 } h ? new[] { h }.Concat(back.Aliases) : back.Aliases)} in DNS");
            ok = true;
        }
        catch (SocketException) { lines.Add($"{ip} has no name in DNS (no reverse record)"); }
        catch (Exception ex) when (ex is not OperationCanceledException) { lines.Add($"The reverse lookup failed: {ex.GetBaseException().Message}"); }

        if (!string.IsNullOrWhiteSpace(name) && name != "—" && !IPAddress.TryParse(name, out _) && name.Length <= 253 && name.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            try
            {
                var fwd = await System.Net.Dns.GetHostAddressesAsync(name, ct);
                var list = fwd.Select(a => a.ToString()).ToList();
                lines.Add($"{name} resolves to {string.Join(", ", list)}");
                lines.Add(list.Contains(ip) ? "That includes this device's address." : "That does NOT include this device's address: the name points somewhere else.");
                ok |= list.Contains(ip);
            }
            catch (SocketException) { lines.Add($"{name} doesn't resolve"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { lines.Add($"The lookup of {name} failed: {ex.GetBaseException().Message}"); }
        }
        return new Result("dns", ip, ok, lines, ok ? "DNS knows this device" : "DNS has nothing that matches this device");
    }

    private async Task<Probe> Safe(IPAddress target, int timeoutMs, int ttl, CancellationToken ct)
    {
        try { return await _probe.SendAsync(target, timeoutMs, ttl, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new Probe(IPStatus.TimedOut, 0, null); }
        catch (PingException) { return new Probe(IPStatus.Unknown, 0, null); }
        catch (SocketException) { return new Probe(IPStatus.Unknown, 0, null); }
    }

    private static string Describe(IPStatus s) => s switch
    {
        IPStatus.TimedOut => "no answer (timed out)",
        IPStatus.DestinationHostUnreachable => "host unreachable",
        IPStatus.DestinationNetworkUnreachable => "network unreachable",
        IPStatus.DestinationUnreachable => "unreachable",
        IPStatus.Unknown => "couldn't send (no permission, or no route)",
        _ => s.ToString(),
    };
}
