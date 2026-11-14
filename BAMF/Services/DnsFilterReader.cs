using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// A reader for a Pi-hole or an AdGuard Home on your network. They answer every DNS question their clients ask, so they know what BAMF can't see
/// on a switched network: which device asks how much, and how much of it was blocked. Every five minutes BAMF asks the one you name for its
/// totals and its busiest clients over the last day, and shows them on Activity beside the device they belong to.
///
/// Off by default, and it needs an address first. It only reads, it never changes a setting on the filter, and it can't see a device that
/// doesn't use it (that is what "Devices that skip your DNS" is for). Pi-hole 6 or newer, whose API changed in 6, and any AdGuard Home. The
/// password or app password is kept like a webhook's: never shown again, not in the settings export. A Pi-hole login is closed again after each
/// read, so BAMF never takes one of its few login seats.
/// </summary>
public sealed class DnsFilterReader : BackgroundService
{
    public sealed record Client(string Ip, string Name, long Queries, long Blocked);
    public sealed record Snapshot(string At, string Provider, long Total, long Blocked, double Percent, List<Client> Clients, string? Error);

    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    public const int MostClients = 30;
    private const int AdGuardLogSample = 1000;

    private readonly HostStore _store;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<DnsFilterReader> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);

    /// <summary>Sends a request and returns the status and body. Swapped out by tests.</summary>
    internal Func<HttpRequestMessage, CancellationToken, Task<(int Status, string Body)>>? Send { get; set; }

    public DnsFilterReader(HostStore store, IHttpClientFactory http, ILogger<DnsFilterReader> log)
    {
        _store = store; _http = http; _log = log;
    }

    public string Provider => _store.GetSetting("dnsFilter") == "adguard" ? "adguard" : "pihole";
    public string Url => (_store.GetSetting("dnsFilterUrl") ?? "").Trim().TrimEnd('/');
    public string User => (_store.GetSetting("dnsFilterUser") ?? "").Trim();
    public string Secret => _store.GetSetting("dnsFilterSecret") ?? "";
    public bool Insecure => _store.GetSetting("dnsFilterInsecure") == "true";
    public bool Enabled => _store.GetSetting("dnsFilterOn") == "true" && Url != "";

    public Snapshot? Last
    {
        get
        {
            try { return JsonSerializer.Deserialize<Snapshot>(_store.GetSetting("dnsFilterSnapshot") ?? "null"); }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>The address as it will be used (the web page's /admin left off), or an error to show.</summary>
    public static (string? Url, string? Error) CleanUrl(string text)
    {
        text = text.Trim().TrimEnd('/');
        if (text.EndsWith("/admin", StringComparison.OrdinalIgnoreCase)) text = text[..^"/admin".Length];
        if (text.Length > 300 || !Uri.TryCreate(text, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) || u.Host == "")
            return (null, "Enter the address of its web page, like http://192.168.2.5 or http://pi.hole:3000.");
        if (u.UserInfo != "") return (null, "Leave the login out of the address: it goes in the boxes below.");
        return (text, null);
    }

    private HttpRequestMessage Req(HttpMethod method, string path, string? sid = null, string? json = null)
    {
        var req = new HttpRequestMessage(method, Url + path);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.UserAgent.ParseAdd("BAMF-Network-Monitor");
        if (sid is not null) req.Headers.Add("sid", sid);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return req;
    }

    private async Task<(int Status, string Body)> Call(HttpRequestMessage req, CancellationToken ct)
    {
        if (Send is { } s) return await s(req, ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        // A filter on a home network often has a self-signed certificate; the switch for that is the person's to flip, per filter.
        using var own = Insecure ? new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator }) : null;
        using var resp = await (own ?? _http.CreateClient()).SendAsync(req, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        return ((int)resp.StatusCode, body);
    }

    // ------------------------------------------------------------ Pi-hole 6

    private async Task<Snapshot> Pihole(CancellationToken ct)
    {
        string? sid = null;
        if (Secret != "")
        {
            var (st, body) = await Call(Req(HttpMethod.Post, "/api/auth", json: JsonSerializer.Serialize(new { password = Secret })), ct);
            if (st is 401 or 403) throw new InvalidOperationException("Pi-hole refused that password. Make an app password under Settings → Web interface / API and use that.");
            if (st == 404) throw new InvalidOperationException("That isn't a Pi-hole 6 or newer: it has no /api/auth. Check the address and port.");
            if (st < 200 || st >= 300) throw new InvalidOperationException($"Pi-hole answered HTTP {st} to the login.");
            using var d = JsonDocument.Parse(body);
            sid = d.RootElement.TryGetProperty("session", out var ses) && ses.TryGetProperty("sid", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (string.IsNullOrEmpty(sid)) throw new InvalidOperationException("Pi-hole didn't start a session: its two-factor login isn't supported here.");
        }
        try
        {
            var summary = await Get(Req(HttpMethod.Get, "/api/stats/summary", sid), "its totals", ct);
            var top = await Get(Req(HttpMethod.Get, $"/api/stats/top_clients?count={MostClients}", sid), "its clients", ct);
            var blocked = await Get(Req(HttpMethod.Get, $"/api/stats/top_clients?blocked=true&count={MostClients}", sid), "its blocked clients", ct);

            var q = summary.RootElement.TryGetProperty("queries", out var qs) ? qs : default;
            long Num(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
            var total = Num(q, "total"); var blockedTotal = Num(q, "blocked");
            var blockedBy = new Dictionary<string, long>();
            if (blocked.RootElement.TryGetProperty("clients", out var bc) && bc.ValueKind == JsonValueKind.Array)
                foreach (var c in bc.EnumerateArray()) if (c.TryGetProperty("ip", out var ip) && ip.GetString() is { } a) blockedBy[a] = Num(c, "count");
            var clients = new List<Client>();
            if (top.RootElement.TryGetProperty("clients", out var tc) && tc.ValueKind == JsonValueKind.Array)
                foreach (var c in tc.EnumerateArray())
                {
                    var addr = c.TryGetProperty("ip", out var ip) ? ip.GetString() ?? "" : "";
                    if (addr == "") continue;
                    clients.Add(new Client(addr, c.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "", Num(c, "count"), blockedBy.GetValueOrDefault(addr)));
                }
            return Make("pihole", total, blockedTotal, clients);
        }
        finally
        {
            if (sid is not null) { try { await Call(Req(HttpMethod.Delete, "/api/auth", sid), ct); } catch (Exception) when (!ct.IsCancellationRequested) { /* it times out by itself */ } }
        }
    }

    // ------------------------------------------------------------ AdGuard Home

    private async Task<Snapshot> AdGuard(CancellationToken ct)
    {
        HttpRequestMessage A(string path)
        {
            var r = Req(HttpMethod.Get, path);
            if (User != "" || Secret != "") r.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(User + ":" + Secret)));
            return r;
        }
        var stats = await Get(A("/control/stats"), "its statistics", ct);
        var log = await Get(A($"/control/querylog?limit={AdGuardLogSample}&response_status=filtered"), "its query log", ct);
        var root = stats.RootElement;
        long Num(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
        var blockedBy = new Dictionary<string, long>();
        if (log.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var e in data.EnumerateArray())
                if (e.TryGetProperty("client", out var c) && c.GetString() is { } ip) blockedBy[ip] = blockedBy.GetValueOrDefault(ip) + 1;
        var clients = new List<Client>();
        if (root.TryGetProperty("top_clients", out var tc) && tc.ValueKind == JsonValueKind.Array)
            foreach (var item in tc.EnumerateArray())
                foreach (var p in item.EnumerateObject())
                    clients.Add(new Client(p.Name, "", p.Value.TryGetInt64(out var n) ? n : 0, blockedBy.GetValueOrDefault(p.Name)));
        return Make("adguard", Num(root, "num_dns_queries"), Num(root, "num_blocked_filtering"), clients);
    }

    private async Task<JsonDocument> Get(HttpRequestMessage req, string what, CancellationToken ct)
    {
        var (st, body) = await Call(req, ct);
        if (st is 401 or 403) throw new InvalidOperationException("It refused the login. Check the user name and password.");
        if (st < 200 || st >= 300) throw new InvalidOperationException($"It answered HTTP {st} when asked for {what}.");
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw new InvalidOperationException($"What it said for {what} wasn't JSON. Check the address and port."); }
    }

    private static Snapshot Make(string provider, long total, long blocked, List<Client> clients) =>
        new(DateTime.UtcNow.ToString("o"), provider, total, blocked, total > 0 ? Math.Round(100.0 * blocked / total, 1) : 0,
            clients.OrderByDescending(c => c.Queries).Take(MostClients).ToList(), null);

    /// <summary>Asks the filter now. Null if there is no address or one read is already running; otherwise what it said, or why not.</summary>
    public async Task<Snapshot?> Refresh(CancellationToken ct)
    {
        if (Url == "") return null;
        if (!await _busy.WaitAsync(0, ct)) return null;
        try
        {
            Snapshot snap;
            try { snap = Provider == "adguard" ? await AdGuard(ct) : await Pihole(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var why = ex is InvalidOperationException ? ex.Message : ex.GetBaseException().Message;
                _log.LogWarning("DNS filter: {Why}", why);
                // What it read last time stays, so a blip doesn't blank the card.
                snap = (Last ?? new Snapshot("", Provider, 0, 0, 0, new(), null)) with { Error = why, At = DateTime.UtcNow.ToString("o"), Provider = Provider };
            }
            _store.SetSetting("dnsFilterSnapshot", JsonSerializer.Serialize(snap));
            return snap;
        }
        finally { _busy.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { if (Enabled) await Refresh(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "The DNS filter reader failed"); }
            try { await Task.Delay(Every, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
