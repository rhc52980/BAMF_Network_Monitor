using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>
/// The Tools tab: what it may do, live ping one echo at a time, trace route, path ping, DNS lookup, an HTTP check, a port check and the "why is it
/// slow?" diagnosis. A device BAMF knows can be the target whenever the tools are on; an address or name that was typed needs "any address" switched
/// on in Settings as well, and, like every POST, the main password (a view-only password can't POST at all). The older text tools stay as they were.
/// </summary>
internal static class ToolsEndpoints
{
    private sealed record Target(IPAddress? Ip, string Label, string? Name, bool Typed, string Raw);

    private static readonly ConcurrentDictionary<string, (IPAddress Ip, DateTime At)> Looked = new(StringComparer.OrdinalIgnoreCase);

    private static HostRecord? KnownByIp(HostStore store, string raw) =>
        IPAddress.TryParse(raw, out var a) && a.AddressFamily == AddressFamily.InterNetwork
            ? store.GetAll().FirstOrDefault(h => !h.Forgotten && h.Ip == a.ToString()) : null;

    private static string DeviceName(HostRecord h) => h.CustomName != "" ? h.CustomName : !string.IsNullOrEmpty(h.Hostname) && h.Hostname != "—" ? h.Hostname : h.Ip;

    private static IResult? Off(HostStore store) =>
        store.GetSetting("networkTools") == "false" ? Results.Conflict(new { error = "Network tools are switched off in Settings." }) : null;

    private static IResult? TypedOff(HostStore store) =>
        store.GetSetting("traceAnywhere") != "true"
            ? Results.Conflict(new { error = "Tools to an address you type are switched off. Pick one of your devices, or switch on Ping and trace route to any address in Settings, System." }) : null;

    /// <summary>The device or typed address a request means. A typed address that is a device BAMF knows is that device.</summary>
    private static (Target? T, IResult? Error) WhoIs(HostStore store, ToolRunRequest body)
    {
        if (body.HostId is { } id)
        {
            var h = store.GetAll().FirstOrDefault(x => x.Id == id && !x.Forgotten);
            if (h is null) return (null, Results.NotFound());
            if (!IPAddress.TryParse(h.Ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork)
                return (null, Results.BadRequest(new { error = "BAMF doesn't have an IPv4 address for that device." }));
            var name = !string.IsNullOrEmpty(h.Hostname) && h.Hostname != "—" ? h.Hostname : h.MdnsName;
            return (new Target(addr, DeviceName(h), string.IsNullOrEmpty(name) ? null : name, false, h.Ip), null);
        }
        var raw = (body.Target ?? "").Trim();
        if (raw.Length == 0) return (null, Results.BadRequest(new { error = "Pick a device or type an address." }));
        if (KnownByIp(store, raw) is { } known && IPAddress.TryParse(known.Ip, out var kip))
            return (new Target(kip, DeviceName(known), !string.IsNullOrEmpty(known.Hostname) && known.Hostname != "—" ? known.Hostname : null, false, raw), null);
        if (TypedOff(store) is { } off) return (null, off);
        if (NetworkTools.CheckTarget(raw) is { } bad) return (null, Results.BadRequest(new { error = bad }));    // before it uses up the wait between typed runs
        return (new Target(null, raw, IPAddress.TryParse(raw, out _) ? null : raw, true, raw), null);
    }

    private static async Task<(IPAddress Ip, string? Note)> IpOf(Target t, NetworkTools tools, CancellationToken ct) =>
        t.Ip is { } ip ? (ip, null) : await tools.ResolveTyped(t.Raw, ct);

    public static void Map(WebApplication app)
    {
        // What the Tools tab can do, and starting points for its quick buttons.
        app.MapGet("/api/tools", (HostStore store, WanWatch wan) =>
        {
            var enabled = store.GetSetting("networkTools") != "false";
            return Results.Json(new
            {
                enabled,
                anywhere = enabled && store.GetSetting("traceAnywhere") == "true",
                gateways = PortChecker.DefaultGateways().ToList(),
                dnsServers = DnsClient.SystemServers().Select(a => a.ToString()).ToList(),
                wanTarget = wan.Target,
                gapSeconds = (int)NetworkTools.AnywhereGap.TotalSeconds,
                maxPorts = NetworkTools.MaxCheckPorts,
                commonPorts = PortChecker.CommonPorts.Select(p => new { p.Port, p.Service }),
            });
        });

        // One echo, for the live ping: the page asks again every second.
        app.MapPost("/api/tools/ping", async (ToolRunRequest body, HostStore store, NetworkTools tools, CancellationToken ct) =>
        {
            if (Off(store) is { } off) return off;
            var (t, err) = WhoIs(store, body);
            if (err is not null) return err;
            try
            {
                IPAddress ip;
                if (t!.Ip is { } known) ip = known;
                else if (Looked.TryGetValue(t.Raw, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(2)) ip = c.Ip;
                else
                {
                    ip = (await tools.ResolveTyped(t.Raw, ct)).Ip;
                    if (Looked.Count > 200) Looked.Clear();
                    Looked[t.Raw] = (ip, DateTime.UtcNow);
                }
                var p = await tools.PingOnce(ip, ct);
                if (p is null) return Results.Json(new { error = "Too many pings at once." }, statusCode: 429);
                var ok = p.Status == IPStatus.Success;
                return Results.Json(new
                {
                    ok, ms = ok ? (long?)p.Ms : null, address = ip.ToString(), from = p.From?.ToString(),
                    status = ok ? "ok" : p.Status == IPStatus.TimedOut ? "timeout" : p.Status is IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable or IPStatus.DestinationUnreachable ? "unreachable" : "error",
                });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/tools/run", async (ToolRunRequest body, HostStore store, NetworkTools tools, CancellationToken ct) =>
        {
            if (Off(store) is { } off) return off;
            var tool = (body.Tool ?? "").Trim().ToLowerInvariant();
            if (tool is not ("trace" or "path" or "dns" or "http" or "port")) return Results.BadRequest(new { error = "Trace, path, dns, http or port." });
            try
            {
                if (tool == "http")
                {
                    var raw = (body.Target ?? "").Trim();
                    var bad = NetworkTools.ParseHttpTarget(raw, out var uri);
                    if (bad is not null) return Results.BadRequest(new { error = bad });
                    var typedHttp = KnownByIp(store, uri.IdnHost) is null;
                    if (typedHttp && TypedOff(store) is { } off2) return off2;
                    var http = await tools.Exclusive<NetworkTools.HttpData>(typedHttp, ct, c => tools.HttpCheck(raw, c), 40);
                    return http is null ? Busy() : Results.Json(new { tool, target = uri.AbsoluteUri, address = (string?)null, note = (string?)null, data = http });
                }

                var (t, err) = WhoIs(store, body);
                if (err is not null) return err;
                string? note = null;
                IPAddress? address = t!.Ip;
                object? data;
                switch (tool)
                {
                    case "trace":
                    case "path":
                        data = await tools.Exclusive<object>(t.Typed, ct, async c =>
                        {
                            var (ip, n) = await IpOf(t, tools, c); note = n; address = ip;
                            return tool == "trace" ? await tools.TraceHops(ip, c) : await tools.PathHops(ip, c);
                        }, tool == "path" ? 100 : 45);
                        break;
                    case "dns":
                        data = await tools.Exclusive<object>(t.Typed, ct, async c =>
                        {
                            // A name is asked of this network's resolver, and (for a typed one only, so a device's name never leaves the house)
                            // of Cloudflare and Google too; an address gets its reverse lookup.
                            if (t.Ip is { } devIp)
                                return new { reverse = await tools.ReverseLookup(devIp, c), compare = t.Name is { } nm && NetworkTools.CheckTarget(nm) is null ? await tools.DnsCompare(nm, false, c) : null };
                            if (IPAddress.TryParse(t.Raw, out var typedIp)) { address = typedIp; return new { reverse = await tools.ReverseLookup(typedIp, c), compare = (NetworkTools.DnsData?)null }; }
                            var bad = NetworkTools.CheckTarget(t.Raw);
                            if (bad is not null) throw new ArgumentException(bad);
                            return new { reverse = (NetworkTools.ReverseData?)null, compare = (NetworkTools.DnsData?)await tools.DnsCompare(t.Raw, true, c) };
                        }, 30);
                        break;
                    default:
                        var portBad = NetworkTools.ParsePorts(body.Ports, out var ports);
                        if (portBad is not null) return Results.BadRequest(new { error = portBad });
                        data = await tools.Exclusive<object>(t.Typed, ct, async c =>
                        {
                            var (ip, n) = await IpOf(t, tools, c); note = n; address = ip;
                            return await tools.PortCheck(ip, ports, c);
                        }, 60);
                        break;
                }
                return data is null ? Busy() : Results.Json(new { tool, target = t.Label, address = address?.ToString(), note, data });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 429); }
        });

        // Router, DNS, the internet and a name, in that order, and where it breaks. Fixed targets, nothing typed.
        app.MapPost("/api/tools/diagnose", async (HostStore store, NetworkTools tools, WanWatch wan, CancellationToken ct) =>
        {
            if (Off(store) is { } off) return off;
            var r = await tools.Exclusive<NetworkTools.DiagData>(false, ct, c => tools.Diagnose(wan.Target, c), 40);
            return r is null ? Busy() : Results.Json(r);
        });
    }

    private static IResult Busy() => Results.Json(new { error = "Another tool is running. Try again in a moment." }, statusCode: 429);
}
