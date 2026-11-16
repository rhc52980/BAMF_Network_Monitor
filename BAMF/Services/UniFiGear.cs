using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LanWatch.Services;

/// <summary>The login every UniFi reader shares: a UniFi OS console first, else a classic controller.</summary>
internal static class UniFiApi
{
    /// <summary>Logs in, and returns the API prefix: "/proxy/network" on a UniFi OS console, "" on a classic controller.</summary>
    public static async Task<string> Login(HttpClient http, string url, string user, string password, CancellationToken ct)
    {
        var creds = JsonSerializer.Serialize(new { username = user, password });
        using (var os = await http.PostAsync(url + "/api/auth/login", new StringContent(creds, Encoding.UTF8, "application/json"), ct))
        {
            if (os.IsSuccessStatusCode)
            {
                if (os.Headers.TryGetValues("X-CSRF-Token", out var csrf)) http.DefaultRequestHeaders.Add("X-CSRF-Token", csrf.First());
                return "/proxy/network";
            }
        }
        using var classic = await http.PostAsync(url + "/api/login", new StringContent(creds, Encoding.UTF8, "application/json"), ct);
        if (classic.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("The UniFi controller refused that login. Check the user name and password.");
        classic.EnsureSuccessStatusCode();
        return "";
    }
}

/// <summary>
/// Reads a UniFi controller for the Network gear features: every adopted switch, access point and gateway
/// (/stat/device), and every connected client (/stat/sta). Read-only: it only GETs, so a view-only local
/// account is all it needs. The login is kept between reads rather than made again each minute.
/// </summary>
public sealed class UniFiGear : IDisposable
{
    public sealed record Settings(string Url, string User, string Secret, string Site, bool Verify);

    /// <summary>Swapped out by tests, to stand in for the controller.</summary>
    internal HttpMessageHandler? Handler { get; set; }

    private HttpClient? _http;
    private string _prefix = "";
    private string _key = "";

    public void Dispose() { _http?.Dispose(); _http = null; }

    private HttpClient NewClient(Settings s)
    {
        if (Handler is { } h) return new HttpClient(h, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        // A console on a home network has a certificate it signed itself.
        if (!s.Verify) handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Logs in if there's no session for these settings yet.</summary>
    private async Task EnsureSession(Settings s, CancellationToken ct)
    {
        var key = $"{s.Url}|{s.User}|{s.Secret}|{s.Site}|{s.Verify}";
        if (_http is null || key != _key) { _http?.Dispose(); _http = null; }
        if (_http is not null) return;
        var http = NewClient(s);
        try { _prefix = await UniFiApi.Login(http, s.Url, s.User, s.Secret, ct); _http = http; _key = key; }
        catch { http.Dispose(); throw; }
    }

    public async Task<GearSnapshot> Read(Settings s, CancellationToken ct)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                await EnsureSession(s, ct);
                var site = s.Site == "" ? "default" : Uri.EscapeDataString(s.Site);
                var (st, devices) = await Get($"{s.Url}{_prefix}/api/s/{site}/stat/device", ct);
                if (st is 401 or 403 && attempt == 0) { _http.Dispose(); _http = null; continue; }   // the session ran out: log in again, once
                var (st2, clients) = st == 200 ? await Get($"{s.Url}{_prefix}/api/s/{site}/stat/sta", ct) : (st, null);
                if (st is 401 or 403 || st2 is 401 or 403)
                    throw new InvalidOperationException("The UniFi controller won't let that account read its devices. Give it the View Only role on the Network application.");
                if (st != 200) throw new InvalidOperationException($"The UniFi controller answered HTTP {st} when asked for its devices.");
                if (st2 != 200) throw new InvalidOperationException($"The UniFi controller answered HTTP {st2} when asked for its clients.");
                return Parse(devices, clients, DateTime.UtcNow);
            }
        }
        catch (Exception) { _http?.Dispose(); _http = null; throw; }
    }

    /// <summary>
    /// The intrusion prevention events the controller has logged, newest first. They're kept with its alarms (key EVT_IPS_*),
    /// so a controller that keeps them somewhere else, or has Threat Management off, gives an empty list rather than an error.
    /// </summary>
    public async Task<List<GearEvent>> ReadThreats(Settings s, CancellationToken ct)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                await EnsureSession(s, ct);
                var site = s.Site == "" ? "default" : Uri.EscapeDataString(s.Site);
                var (st, body) = await Get($"{s.Url}{_prefix}/api/s/{site}/stat/alarm?_limit=200&_sort=-time", ct);
                if (st is 401 or 403 && attempt == 0) { _http!.Dispose(); _http = null; continue; }
                if (st is 404) return new();
                if (st != 200) throw new InvalidOperationException($"The UniFi controller answered HTTP {st} when asked for its alarms.");
                return ParseThreats(body);
            }
        }
        catch (Exception) { _http?.Dispose(); _http = null; throw; }
    }

    /// <summary>Turns the controller's alarm list into threat events. Pure, for the tests.</summary>
    public static List<GearEvent> ParseThreats(JsonNode? alarms)
    {
        var list = new List<GearEvent>();
        foreach (var a in Data(alarms))
        {
            if (!Str(a, "key").StartsWith("EVT_IPS", StringComparison.OrdinalIgnoreCase)) continue;
            var ms = Num(a, "time");
            if (ms is null) continue;
            var title = Str(a, "inner_alert_signature") is { Length: > 0 } sig ? sig : Str(a, "msg") is { Length: > 0 } m ? m : "Intrusion prevention event";
            var cat = Str(a, "inner_alert_category");
            var src = Str(a, "src_ip") is { Length: > 0 } sp ? sp + (Str(a, "src_port") is { Length: > 0 } spt ? ":" + spt : "") : "";
            var dst = Str(a, "dst_ip") is { Length: > 0 } dp ? dp : Str(a, "dest_ip");
            var action = Str(a, "inner_alert_action");
            var blocked = action.Equals("blocked", StringComparison.OrdinalIgnoreCase) || action.Equals("drop", StringComparison.OrdinalIgnoreCase) || action.Equals("reject", StringComparison.OrdinalIgnoreCase);
            list.Add(new GearEvent("unifi", DateTimeOffset.FromUnixTimeMilliseconds(ms.Value).UtcDateTime.ToString("o"), "threat", title,
                (cat != "" ? cat + ". " : "") + (src != "" || dst != "" ? $"{(src != "" ? src : "?")} to {(dst != "" ? dst : "?")}" : "") + (action != "" ? $" ({action})" : ""), blocked));
        }
        return list.OrderByDescending(e => e.At, StringComparer.Ordinal).ToList();
    }

    private async Task<(int Status, JsonNode? Body)> Get(string url, CancellationToken ct)
    {
        using var resp = await _http!.GetAsync(url, ct);
        var code = (int)resp.StatusCode;
        if (code != 200) return (code, null);
        try { return (200, JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))); }
        catch (JsonException) { throw new InvalidOperationException("What the UniFi controller said wasn't JSON. Check the address: it should be the console's own, like https://192.168.1.1."); }
    }

    // ------------------------------------------------------------ reading what it said

    /// <summary>Turns the controller's two lists into what the gear features use. Pure, so it's tested without a controller.</summary>
    public static GearSnapshot Parse(JsonNode? devices, JsonNode? clients, DateTime now)
    {
        var gear = new List<GearDevice>();
        var names = new Dictionary<string, string>();
        foreach (var d in Data(devices))
        {
            var mac = RouterImport.NormMac(Str(d, "mac"));
            if (mac == "") continue;
            var kind = KindOf(d);
            if (kind is null) continue;
            var name = Str(d, "name") is { Length: > 0 } n ? n : Str(d, "model") is { Length: > 0 } m ? m : mac;
            names[mac] = name;

            var ports = new List<GearPort>();
            double poe = 0; var anyPoe = false;
            foreach (var p in Arr(d, "port_table"))
            {
                var idx = (int?)Num(p, "port_idx") ?? 0;
                if (idx < 1 || idx > HostStore.MaxSwitchPorts) continue;
                var watts = Dbl(p, "poe_power") ?? 0;
                if (watts > 0) { poe += watts; anyPoe = true; }
                ports.Add(new GearPort(idx, Str(p, "name"), Bool(p, "up"), (int)(Num(p, "speed") ?? 0), watts));
            }
            var budget = Dbl(d, "total_max_power");
            var sys = d?["system-stats"];
            double? temp = Dbl(d, "general_temperature");
            if (temp is null)
            {
                var all = Arr(d, "temperatures").Select(t => Dbl(t, "value")).Where(v => v is not null).Select(v => v!.Value).ToList();
                if (all.Count > 0) temp = all.Max();
            }
            var ssids = new List<GearSsid>();
            if (kind == "ap")
                foreach (var g in Arr(d, "vap_table").Where(v => Str(v, "essid") != "").GroupBy(v => Str(v, "essid")))
                    ssids.Add(new GearSsid(g.Key, string.Join(", ", g.Select(v => Band(Str(v, "radio"))).Where(b => b != "").Distinct().OrderBy(b => b))));
            var up = d?["uplink"];
            // The gateway's internet connections (wan1, wan2...) and which one carries traffic.
            var wans = new List<GearWan>();
            if (kind == "router" && d is JsonObject obj)
                foreach (var (k, w) in obj.Where(p => System.Text.RegularExpressions.Regex.IsMatch(p.Key, "^wan[0-9]*$") && p.Value is JsonObject))
                    wans.Add(new GearWan(Str(w, "name") is { Length: > 0 } wn ? wn : k.ToUpperInvariant(), Bool(w, "up"), Str(w, "ip"), Bool(w, "is_uplink")));

            gear.Add(new GearDevice("unifi", mac, name, kind, Str(d, "model"), Str(d, "version"),
                Bool(d, "upgradable") ? (Str(d, "upgrade_to_firmware") is { Length: > 0 } u ? u : "newer") : "",
                (long)(Num(d, "uptime") ?? Num(sys, "uptime") ?? 0), Num(d, "state") is null or 1, Str(d, "ip"), ports.Count == 0 ? 0 : ports.Max(p => p.Port),
                kind == "router" ? "" : RouterImport.NormMac(Str(up, "uplink_mac")), kind == "router" ? 0 : (int)(Num(up, "uplink_remote_port") ?? 0),
                Dbl(sys, "cpu"), Dbl(sys, "mem"), temp, anyPoe ? Math.Round(poe, 1) : null, budget is > 0 ? budget : null,
                ports, ssids, wans));
        }

        var people = new List<GearClient>();
        foreach (var c in Data(clients))
        {
            var mac = RouterImport.NormMac(Str(c, "mac"));
            if (mac == "" || names.ContainsKey(mac)) continue;          // the gear itself isn't a client
            var wired = Bool(c, "is_wired");
            var ip = Str(c, "ip") is { Length: > 0 } i ? i : Str(c, "last_ip");
            if (wired)
            {
                var via = RouterImport.NormMac(Str(c, "sw_mac")) is { Length: > 0 } sw ? sw : RouterImport.NormMac(Str(c, "last_uplink_mac"));
                people.Add(new GearClient("unifi", mac, ip, true, via, (int)(Num(c, "sw_port") ?? 0), "", "", 0, null, null, null, null, null, 0, ClientName(c), Str(c, "oui")));
                continue;
            }
            // dBm, negative: 0 or missing means the controller has no reading. Failing that, it's the noise floor plus the signal-to-noise figure.
            int? signal = Num(c, "signal") is { } sg and < 0 ? (int)sg : Num(c, "rssi") is { } r && Num(c, "noise") is { } nz and < 0 ? (int)(nz + r) : null;
            int? Mbps(string key) => Num(c, key) is { } kbps and > 0 ? (int)Math.Round(kbps / 1000.0) : null;
            people.Add(new GearClient("unifi", mac, ip, false, RouterImport.NormMac(Str(c, "ap_mac")), 0, Str(c, "essid"), Band(Str(c, "radio")),
                (int)(Num(c, "channel") ?? 0), signal, Num(c, "noise") is { } noise and < 0 ? (int)noise : null, Mbps("tx_rate"), Mbps("rx_rate"),
                Num(c, "satisfaction") is { } sat and >= 0 and <= 100 ? (int)sat : null, (int)(Num(c, "roam_count") ?? 0), ClientName(c), Str(c, "oui")));
        }
        return new GearSnapshot("unifi", now.ToString("o"), gear, people);
    }

    /// <summary>What sort of gear a controller's device is, or null for something BAMF has no use for (a phone, a camera).</summary>
    private static string? KindOf(JsonNode? d)
    {
        var type = Str(d, "type").ToLowerInvariant();
        if (type is "ugw" or "udm" or "uxg" or "ucg" or "udr" || type.StartsWith("udm") || type.StartsWith("uxg") || type.StartsWith("ucg")) return "router";
        if (type is "uap" or "ubb") return "ap";
        if (type == "usw") return "switch";
        if (Arr(d, "vap_table").Any()) return "ap";
        if (Arr(d, "port_table").Any()) return "switch";
        return null;
    }

    /// <summary>The name you gave a client in UniFi, else the name it asked for.</summary>
    private static string ClientName(JsonNode? c) => Str(c, "name") is { Length: > 0 } n ? n : Str(c, "hostname");

    private static string Band(string radio) => radio switch { "ng" => "2.4 GHz", "na" => "5 GHz", "6e" or "6g" => "6 GHz", _ => "" };

    private static IEnumerable<JsonNode> Data(JsonNode? root) => (root?["data"] as JsonArray)?.Where(n => n is not null).Select(n => n!) ?? Enumerable.Empty<JsonNode>();
    private static IEnumerable<JsonNode> Arr(JsonNode? n, string key) => (n?[key] as JsonArray)?.Where(x => x is not null).Select(x => x!) ?? Enumerable.Empty<JsonNode>();

    private static string Str(JsonNode? n, string key)
    {
        try { return n?[key]?.GetValue<string>()?.Trim() ?? ""; }
        catch (InvalidOperationException) { return n?[key]?.ToString().Trim() ?? ""; }
    }

    // A number, whether the controller sent it as one or as text ("3.5", which it does for the CPU and a port's PoE draw).
    private static double? Dbl(JsonNode? n, string key)
    {
        var v = n?[key];
        if (v is null) return null;
        try { return v.GetValue<double>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
        }
    }

    private static long? Num(JsonNode? n, string key) => Dbl(n, key) is { } d && !double.IsNaN(d) ? (long)Math.Round(d) : null;

    private static bool Bool(JsonNode? n, string key)
    {
        var v = n?[key];
        if (v is null) return false;
        try { return v.GetValue<bool>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return v.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) || v.ToString() == "1"; }
    }
}
