using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LanWatch.Services;

/// <summary>
/// Turns what a gateway reports into things worth an alert, whichever make it is. A source gives the state of each internet
/// connection and the intrusion events it has logged; this works out what changed.
/// </summary>
public static class GatewayEvents
{
    public sealed record WanState(bool Up, bool Active);

    /// <summary>gateway -> connection -> state, as of the last read. Saved between reads so a restart doesn't announce what was already so.</summary>
    public sealed class WanMemory : Dictionary<string, Dictionary<string, WanState>> { }

    /// <summary>
    /// Compares each gateway's connections with how they were last read. A connection that went down or came back, and traffic that
    /// moved from one connection to another (a failover), are events. The first time a gateway is seen it's only remembered.
    /// </summary>
    public static (WanMemory Next, List<GearEvent> Events) Step(WanMemory before, IEnumerable<GearDevice> gateways, DateTime now)
    {
        var next = new WanMemory();
        var events = new List<GearEvent>();
        var at = now.ToString("o");
        foreach (var g in gateways.Where(g => g.Wans is { Count: > 0 }))
        {
            var key = g.Source + "|" + g.Mac;
            var current = g.Wans!.GroupBy(w => w.Name).ToDictionary(x => x.Key, x => new WanState(x.First().Up, x.First().Active));
            next[key] = current;
            if (!before.TryGetValue(key, out var was)) continue;
            foreach (var (name, state) in current)
            {
                if (!was.TryGetValue(name, out var prev)) continue;
                if (prev.Up && !state.Up)
                    events.Add(new GearEvent(g.Source, at, "wan", $"Internet connection {name} is down on {g.Name}",
                        $"{g.Name} reports that its {name} connection has lost its link" + (current.Any(c => c.Value.Up && c.Key != name) ? "; another connection is still up, so traffic may have moved to it." : ", and no other connection is up.")));
                else if (!prev.Up && state.Up)
                    events.Add(new GearEvent(g.Source, at, "wan", $"Internet connection {name} is back on {g.Name}", $"{g.Name} reports that its {name} connection has a link again."));
            }
            // Traffic moved: one connection was carrying it before and another is now.
            var from = was.Where(w => w.Value.Active).Select(w => w.Key).ToList();
            var to = current.Where(w => w.Value.Active).Select(w => w.Key).ToList();
            if (from.Count == 1 && to.Count == 1 && from[0] != to[0])
                events.Add(new GearEvent(g.Source, at, "wan", $"{g.Name} moved your internet from {from[0]} to {to[0]}",
                    $"Traffic was using the {from[0]} connection and is now using {to[0]}: a failover, or a return to the main connection. " +
                    "If this isn't expected, check the connection that was in use."));
        }
        return (next, events);
    }

    /// <summary>One alert for a batch of threat events: what was seen most, from where, and how many were stopped.</summary>
    public static (string Title, string Detail) Summarize(IReadOnlyList<GearEvent> events)
    {
        var blocked = events.Count(e => e.Blocked);
        if (events.Count == 1)
            return ((blocked == 1 ? "Your gateway blocked a threat: " : "Your gateway detected a threat: ") + events[0].Title, events[0].Detail);
        var top = events.GroupBy(e => e.Title).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(5)
            .Select(g => $"{g.Count()} x {g.Key}").ToList();
        var title = $"Your gateway's intrusion detection saw {events.Count} threats ({blocked} blocked)";
        var detail = "Most often: " + string.Join("; ", top) + (events.GroupBy(e => e.Title).Count() > top.Count ? "; and others" : "") + ". " +
                     (blocked < events.Count ? $"{events.Count - blocked} were only detected, not stopped: the gateway may be set to alert and not block. " : "") +
                     "Ordinary scanning from the internet looks like this too; it is worth a look when it is new or aimed at one device.";
        return (title, detail);
    }
}

/// <summary>
/// An OPNsense firewall, read for the network gear features: its ARP table (every device on every network it routes, VLANs
/// included), the state of its gateways (internet connections), and the intrusion detection alerts it has logged. Uses the
/// API key and secret already given to Router import (Kind opnsense in appsettings.json), and only reads.
/// </summary>
public sealed class OpnSenseGear
{
    public sealed record Settings(string Url, string Key, string Secret, bool Verify);

    /// <summary>Swapped out by tests, to stand in for the firewall.</summary>
    internal HttpMessageHandler? Handler { get; set; }

    private HttpClient Client(Settings s)
    {
        HttpClient http;
        if (Handler is { } h) http = new HttpClient(h, disposeHandler: false);
        else
        {
            var handler = new HttpClientHandler();
            if (!s.Verify) handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            http = new HttpClient(handler);
        }
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.Key}:{s.Secret}")));
        return http;
    }

    private static async Task<JsonNode?> Send(HttpClient http, HttpMethod method, string url, string? json, string what, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException($"OPNsense refused the API key for {what}. Give its user the privilege for it (System, Access, Users).");
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException($"This OPNsense has no API for {what}.");
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"OPNsense answered HTTP {(int)resp.StatusCode} when asked for {what}.");
        try { return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)); }
        catch (JsonException) { throw new InvalidOperationException($"What OPNsense said for {what} wasn't JSON. Check the address."); }
    }

    /// <summary>Every device the firewall holds an ARP entry for.</summary>
    public async Task<List<GearHost>> ReadHosts(Settings s, CancellationToken ct)
    {
        using var http = Client(s);
        var node = await Send(http, HttpMethod.Get, s.Url + "/api/diagnostics/interface/getArp", null, "its ARP table", ct);
        return ParseHosts(node);
    }

    public static List<GearHost> ParseHosts(JsonNode? arp)
    {
        var list = new List<GearHost>();
        foreach (var r in (arp as JsonArray) ?? new JsonArray())
        {
            var mac = RouterImport.NormMac(Str(r, "mac"));
            var ip = Str(r, "ip");
            if (mac == "" || !IPAddress.TryParse(ip, out _)) continue;
            var name = Str(r, "hostname");
            if (name.Contains('.')) name = name[..name.IndexOf('.')];
            list.Add(new GearHost("opnsense", mac, ip, name, Str(r, "manufacturer")));
        }
        return list;
    }

    /// <summary>The firewall's internet connections (its gateways), as one gateway with a connection each.</summary>
    public async Task<GearDevice> ReadGateway(Settings s, CancellationToken ct)
    {
        using var http = Client(s);
        var node = await Send(http, HttpMethod.Get, s.Url + "/api/routes/gateway/status", null, "its gateway status", ct);
        return ParseGateway(node, s.Url);
    }

    public static GearDevice ParseGateway(JsonNode? status, string url)
    {
        var wans = new List<GearWan>();
        foreach (var g in (status?["items"] as JsonArray) ?? new JsonArray())
        {
            var name = Str(g, "name");
            if (name == "") continue;
            // "none" is a healthy gateway; "loss" and "delay" are up but unwell; "down" and "force_down" have no link.
            var st = Str(g, "status").ToLowerInvariant();
            wans.Add(new GearWan(name, st is not ("down" or "force_down"), Str(g, "address"), false));
        }
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
        return new GearDevice("opnsense", "opnsense:" + host, "OPNsense", "router", "", "", "", 0, true, host, 0, "", 0, null, null, null, null, null, new(), new(), wans);
    }

    /// <summary>The intrusion detection alerts it has logged, newest first. An error if the IDS isn't installed or its API differs.</summary>
    public async Task<List<GearEvent>> ReadThreats(Settings s, CancellationToken ct)
    {
        using var http = Client(s);
        var node = await Send(http, HttpMethod.Post, s.Url + "/api/ids/service/queryAlerts", """{"current":1,"rowCount":100,"searchPhrase":"","sort":{"timestamp":"desc"}}""", "its intrusion detection alerts", ct);
        return ParseThreats(node);
    }

    public static List<GearEvent> ParseThreats(JsonNode? alerts)
    {
        var list = new List<GearEvent>();
        foreach (var r in (alerts?["rows"] as JsonArray) ?? new JsonArray())
        {
            // OPNsense writes the offset without a colon (+0000), which not every parser takes.
            var stamp = System.Text.RegularExpressions.Regex.Replace(Str(r, "timestamp"), @"([+-]\d\d)(\d\d)$", "$1:$2");
            if (!DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var when)) continue;
            var title = Str(r, "alert") is { Length: > 0 } a ? a : "Intrusion detection alert";
            var src = Str(r, "src_ip") + (Str(r, "src_port") is { Length: > 0 } sp ? ":" + sp : "");
            var action = Str(r, "alert_action");
            var blocked = action.Equals("blocked", StringComparison.OrdinalIgnoreCase) || action.Equals("drop", StringComparison.OrdinalIgnoreCase);
            list.Add(new GearEvent("opnsense", when.UtcDateTime.ToString("o"), "threat", title,
                (Str(r, "alert_category") is { Length: > 0 } c ? c + ". " : "") + $"{(src != "" ? src : "?")} to {(Str(r, "dest_ip") is { Length: > 0 } d ? d : "?")}" + (action != "" ? $" ({action})" : ""), blocked));
        }
        return list.OrderByDescending(e => e.At, StringComparer.Ordinal).ToList();
    }

    private static string Str(JsonNode? n, string key)
    {
        try { return n?[key]?.GetValue<string>()?.Trim() ?? ""; }
        catch (InvalidOperationException) { return n?[key]?.ToString().Trim() ?? ""; }
    }
}
