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
public sealed class NetworkTools
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

    public NetworkTools(IProbe? probe = null) { _probe = probe ?? new SystemProbe(); }

    public sealed record Result(string Tool, string Target, bool Ok, IReadOnlyList<string> Lines, string Summary);

    /// <summary>True when another tool is running; the caller says "busy". Tools wait for no one.</summary>
    public async Task<Result?> RunAsync(string tool, string ip, string? name, CancellationToken ct)
    {
        if (!await _one.WaitAsync(0, ct)) return null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(45));
            return tool switch
            {
                "ping" => await Ping(ip, limit.Token),
                "trace" => await Trace(ip, limit.Token),
                "dns" => await Dns(ip, name, limit.Token),
                _ => throw new ArgumentException("Unknown tool"),
            };
        }
        finally { _one.Release(); }
    }

    public static readonly string[] Tools = { "ping", "trace", "dns" };

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
