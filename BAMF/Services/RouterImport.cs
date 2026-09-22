using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LanWatch.Services;

/// <summary>
/// Device names from your router. The router hands out the addresses and
/// usually knows each device by the name it asked for, or a name you gave it
/// in the router's own pages. BAMF reads that list every hour and uses a name
/// for any device it has no other name for; one button copies them into
/// BAMF's own names.
///
/// Off unless Bamf:RouterImport:Kind is set. It needs the router's credentials,
/// so they live in appsettings.json, not the dashboard:
///   openwrt   ubus JSON-RPC with a username and password (luci-rpc getDHCPLeases)
///   opnsense  an API key and secret (ISC DHCP leases, or Kea's)
///   pfsense   the REST API package, with an API key
///   unifi     a UniFi OS console or a classic controller, with a username and password
/// Routers usually have a self-signed certificate, so certificate errors are
/// ignored unless VerifyCertificate is true.
///
/// A UniFi controller also counts every client's bytes, wired and wireless,
/// which is the whole network's traffic per device without a mirror port. With
/// Traffic left on (the default) BAMF reads those counts once a minute and uses
/// them in place of what its own capture sees. OpenWrt, OPNsense and pfSense
/// don't keep a per-device count in the APIs used here, so they give names only.
/// </summary>
public sealed class RouterImport : BackgroundService
{
    public sealed record Lease(string Mac, string Name, string Ip);
    public sealed record Status(string Kind, string Host, bool Enabled, string? LastRun, int Count, string? Error,
        bool Traffic, string? TrafficAt, int TrafficClients, string? TrafficError);

    private readonly HostStore _store;
    private readonly IConfiguration _config;
    private readonly ILogger<RouterImport> _log;
    private readonly MeasuredTraffic _measured;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private string? _lastRun, _lastError;
    private int _lastCount;

    // The traffic poll keeps its UniFi session between minutes rather than
    // logging in sixty times an hour.
    private HttpClient? _uni;
    private string _uniPrefix = "";
    private readonly Dictionary<string, (long Rx, long Tx, DateTime At)> _uniLast = new(StringComparer.OrdinalIgnoreCase);
    private string? _trafficAt, _trafficError;
    private int _trafficClients;

    public RouterImport(HostStore store, IConfiguration config, ILogger<RouterImport> log, MeasuredTraffic measured)
    {
        _store = store; _config = config; _log = log; _measured = measured;
    }

    /// <summary>Per-client byte counts, from a UniFi controller only. On unless Bamf:RouterImport:Traffic is false.</summary>
    public bool TrafficEnabled => Enabled && Kind == "unifi" && Cfg.GetValue("Traffic", true);

    private IConfigurationSection Cfg => _config.GetSection("Bamf:RouterImport");
    public string Kind => (Cfg["Kind"] ?? "").Trim().ToLowerInvariant();
    public bool Enabled => Kind is "openwrt" or "opnsense" or "pfsense" or "unifi" && !string.IsNullOrWhiteSpace(Cfg["Url"]);

    public Status Current => new(Kind, Uri.TryCreate(Cfg["Url"] ?? "", UriKind.Absolute, out var u) ? u.Host : "", Enabled,
        _lastRun, _lastCount, _lastError, TrafficEnabled, _trafficAt, _trafficClients, _trafficError);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var traffic = TrafficLoop(ct);
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            if (Enabled) await Run(ct);
            var minutes = Math.Clamp(Cfg.GetValue("IntervalMinutes", 60), 5, 24 * 60);
            try { await Task.Delay(TimeSpan.FromMinutes(minutes), ct); } catch (OperationCanceledException) { break; }
        }
        await traffic;
    }

    private async Task TrafficLoop(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(40), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            if (TrafficEnabled)
            {
                try { await PollTraffic(ct); _trafficError = null; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _trafficError = ex.GetBaseException().Message;
                    _uni?.Dispose(); _uni = null;
                    _log.LogInformation("UniFi traffic read failed: {Error}", _trafficError);
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Every connected client's byte counts, turned into what each sent and got
    /// since the last minute. UniFi counts from its own side, the access point
    /// or switch port: its tx_bytes is what it sent to the client, so the
    /// client's download. A wired client's figures are its wired-* ones.
    /// </summary>
    private async Task PollTraffic(CancellationToken ct)
    {
        var site = Cfg["Site"] is { Length: > 0 } s ? s : "default";
        for (var attempt = 0; ; attempt++)
        {
            if (_uni is null)
            {
                var http = Client(new CookieContainer());
                try { _uniPrefix = await UniFiLogin(http, ct); _uni = http; }
                catch { http.Dispose(); throw; }
            }
            using var resp = await _uni.GetAsync(Url + _uniPrefix + $"/api/s/{site}/stat/sta", ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && attempt == 0)
            {
                // The session ran out: log in again, once.
                _uni.Dispose(); _uni = null;
                continue;
            }
            resp.EnsureSuccessStatusCode();
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            var now = DateTime.UtcNow;
            var seen = 0;
            foreach (var c in node?["data"]?.AsArray() ?? new JsonArray())
            {
                var mac = NormMac(Str(c, "mac"));
                if (mac == "") continue;
                var wired = c?["is_wired"]?.GetValueKind() == JsonValueKind.True;
                long? toClient = Num(c, wired ? "wired-tx_bytes" : "tx_bytes") ?? Num(c, "tx_bytes");
                long? fromClient = Num(c, wired ? "wired-rx_bytes" : "rx_bytes") ?? Num(c, "rx_bytes");
                if (toClient is not { } rx || fromClient is not { } tx) continue;
                seen++;
                if (_uniLast.TryGetValue(mac, out var prev))
                {
                    // Counts start again from zero when a client reconnects, so a
                    // count that went down is all new since then.
                    var dRx = rx >= prev.Rx ? rx - prev.Rx : rx;
                    var dTx = tx >= prev.Tx ? tx - prev.Tx : tx;
                    var secs = (now - prev.At).TotalSeconds;
                    if (secs > 1) _measured.Report(mac, "router", "UniFi" + (wired ? ", wired" : ", wireless"), dRx, dTx, secs);
                }
                _uniLast[mac] = (rx, tx, now);
            }
            _trafficClients = seen;
            _trafficAt = now.ToString("o");
            return;
        }
    }

    private static long? Num(JsonNode? n, string key)
    {
        try { return n?[key]?.GetValue<long>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return long.TryParse(n?[key]?.ToString(), out var v) ? v : null;
        }
    }

    /// <summary>Reads the router's list now. Returns how many names it learned, or -1 on failure.</summary>
    public async Task<int> Run(CancellationToken ct)
    {
        if (!Enabled) { _lastError = "Not set up: add Bamf:RouterImport to appsettings.json."; return -1; }
        if (!await _busy.WaitAsync(0, ct)) return _lastCount;
        try
        {
            var leases = await Fetch(ct);
            var named = leases.Where(l => l.Mac != "" && l.Name != "").ToList();
            _store.SaveRouterNames(named.Select(l => (l.Mac, l.Name, l.Ip)), Kind);
            _lastCount = named.Count;
            _lastError = null;
            _log.LogInformation("Router import ({Kind}): {Count} named device(s)", Kind, named.Count);
            return named.Count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _lastError = ex.GetBaseException().Message;
            _log.LogWarning("Router import ({Kind}) failed: {Error}", Kind, _lastError);
            return -1;
        }
        finally
        {
            _lastRun = DateTime.UtcNow.ToString("o");
            _busy.Release();
        }
    }

    private HttpClient Client(CookieContainer? cookies = null)
    {
        var handler = new HttpClientHandler { CookieContainer = cookies ?? new CookieContainer(), UseCookies = true };
        if (!Cfg.GetValue("VerifyCertificate", false))
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private string Url => (Cfg["Url"] ?? "").TrimEnd('/');

    private Task<List<Lease>> Fetch(CancellationToken ct) => Kind switch
    {
        "openwrt" => OpenWrt(ct),
        "opnsense" => OpnSense(ct),
        "pfsense" => PfSense(ct),
        "unifi" => UniFi(ct),
        _ => throw new InvalidOperationException("Unknown router kind"),
    };

    // ------------------------------------------------------------ OpenWrt

    private async Task<List<Lease>> OpenWrt(CancellationToken ct)
    {
        using var http = Client();
        async Task<JsonNode?> Call(string session, string obj, string method, object args)
        {
            var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "call", @params = new object[] { session, obj, method, args } });
            using var resp = await http.PostAsync(Url + "/ubus", new StringContent(body, Encoding.UTF8, "application/json"), ct);
            resp.EnsureSuccessStatusCode();
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (node?["error"] is { } err) throw new InvalidOperationException("ubus: " + (err["message"]?.GetValue<string>() ?? err.ToJsonString()));
            var result = node?["result"]?.AsArray();
            if (result is null || result.Count == 0 || result[0]?.GetValue<int>() != 0)
                throw new InvalidOperationException(method == "login"
                    ? "OpenWrt refused the login: check the username and password."
                    : $"ubus {obj}.{method} refused (code {result?[0]}). The user needs the luci-rpc ACL (package rpcd-mod-luci).");
            return result.Count > 1 ? result[1] : null;
        }
        var login = await Call("00000000000000000000000000000000", "session", "login",
            new { username = Cfg["Username"] ?? "root", password = Cfg["Password"] ?? "" });
        var session = login?["ubus_rpc_session"]?.GetValue<string>() ?? throw new InvalidOperationException("OpenWrt login gave no session.");
        var data = await Call(session, "luci-rpc", "getDHCPLeases", new { });
        var list = new List<Lease>();
        foreach (var l in data?["dhcp_leases"]?.AsArray() ?? new JsonArray())
            list.Add(new Lease(NormMac(Str(l, "macaddr")), CleanName(Str(l, "hostname")), Str(l, "ipaddr")));
        return list;
    }

    // ------------------------------------------------------------ OPNsense

    private async Task<List<Lease>> OpnSense(CancellationToken ct)
    {
        using var http = Client();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Cfg["ApiKey"]}:{Cfg["ApiSecret"]}")));
        // The ISC DHCP server's leases, or Kea's on newer installs.
        foreach (var path in new[] { "/api/dhcpv4/leases/searchLease", "/api/kea/leases4/search" })
        {
            using var resp = await http.GetAsync(Url + path, ct);
            if (resp.StatusCode is HttpStatusCode.NotFound) continue;
            resp.EnsureSuccessStatusCode();
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            var list = new List<Lease>();
            foreach (var r in node?["rows"]?.AsArray() ?? new JsonArray())
            {
                var name = Str(r, "descr") is { Length: > 0 } d ? d : Str(r, "hostname");
                list.Add(new Lease(NormMac(Str(r, "hwaddr")), CleanName(name), Str(r, "address")));
            }
            return list;
        }
        throw new InvalidOperationException("OPNsense has neither the ISC nor the Kea lease API.");
    }

    // ------------------------------------------------------------ pfSense (REST API package)

    private async Task<List<Lease>> PfSense(CancellationToken ct)
    {
        using var http = Client();
        http.DefaultRequestHeaders.Add("X-API-Key", Cfg["ApiKey"] ?? "");
        foreach (var path in new[] { "/api/v2/status/dhcp_server/leases", "/api/v1/services/dhcpd/lease" })
        {
            using var resp = await http.GetAsync(Url + path, ct);
            if (resp.StatusCode is HttpStatusCode.NotFound) continue;
            resp.EnsureSuccessStatusCode();
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            var list = new List<Lease>();
            foreach (var r in node?["data"]?.AsArray() ?? new JsonArray())
            {
                var name = Str(r, "descr") is { Length: > 0 } d ? d : Str(r, "hostname");
                list.Add(new Lease(NormMac(Str(r, "mac")), CleanName(name), Str(r, "ip")));
            }
            return list;
        }
        throw new InvalidOperationException("pfSense has no REST API here. Install the pfSense REST API package.");
    }

    // ------------------------------------------------------------ UniFi

    private async Task<List<Lease>> UniFi(CancellationToken ct)
    {
        var cookies = new CookieContainer();
        using var http = Client(cookies);
        var site = Cfg["Site"] is { Length: > 0 } s ? s : "default";
        var prefix = await UniFiLogin(http, ct);
        var byMac = new Dictionary<string, Lease>(StringComparer.OrdinalIgnoreCase);
        // Every known client, named or not, then the connected ones for fresh addresses.
        foreach (var path in new[] { $"/api/s/{site}/rest/user", $"/api/s/{site}/stat/sta" })
        {
            using var resp = await http.GetAsync(Url + prefix + path, ct);
            if (!resp.IsSuccessStatusCode) continue;
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            foreach (var c in node?["data"]?.AsArray() ?? new JsonArray())
            {
                var mac = NormMac(Str(c, "mac"));
                if (mac == "") continue;
                var name = Str(c, "name") is { Length: > 0 } n ? n : Str(c, "hostname");
                var ip = Str(c, "ip") is { Length: > 0 } i ? i : Str(c, "last_ip");
                var prev = byMac.GetValueOrDefault(mac);
                byMac[mac] = new Lease(mac, CleanName(name) is { Length: > 0 } nn ? nn : prev?.Name ?? "", ip != "" ? ip : prev?.Ip ?? "");
            }
        }
        return byMac.Values.ToList();
    }

    /// <summary>Logs in to a UniFi OS console (UDM, Cloud Key Gen2+), else a classic controller. Returns the API prefix.</summary>
    private async Task<string> UniFiLogin(HttpClient http, CancellationToken ct)
    {
        var creds = JsonSerializer.Serialize(new { username = Cfg["Username"] ?? "", password = Cfg["Password"] ?? "" });
        using (var os = await http.PostAsync(Url + "/api/auth/login", new StringContent(creds, Encoding.UTF8, "application/json"), ct))
        {
            if (os.IsSuccessStatusCode)
            {
                if (os.Headers.TryGetValues("X-CSRF-Token", out var csrf)) http.DefaultRequestHeaders.Add("X-CSRF-Token", csrf.First());
                return "/proxy/network";
            }
        }
        using var classic = await http.PostAsync(Url + "/api/login", new StringContent(creds, Encoding.UTF8, "application/json"), ct);
        classic.EnsureSuccessStatusCode();
        return "";
    }

    // ------------------------------------------------------------ helpers

    private static string Str(JsonNode? n, string key)
    {
        try { return n?[key]?.GetValue<string>()?.Trim() ?? ""; }
        catch (InvalidOperationException) { return n?[key]?.ToString().Trim() ?? ""; }
    }

    public static string NormMac(string mac)
    {
        var hex = new string((mac ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length == 12 ? string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))) : "";
    }

    /// <summary>A lease's name as a person would want it: no "*" placeholders, no domain.</summary>
    private static string CleanName(string name)
    {
        var n = (name ?? "").Trim();
        if (n is "*" or "-" or "?" || n.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return "";
        var dot = n.IndexOf('.');
        if (dot > 0 && !n.Contains(' ') && !IPAddress.TryParse(n, out _)) n = n[..dot];   // a hostname's domain, not a sentence's full stop
        return n.Length > 60 ? n[..60] : n;
    }
}
