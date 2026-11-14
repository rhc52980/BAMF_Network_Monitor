using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// A dynamic DNS hook. When the address the internet sees this network at changes, BAMF calls a URL you give it, with the new address
/// filled in where you wrote <c>{ip}</c>: the update address a DuckDNS, No-IP, Dynu or Cloudflare-script account hands you, so a name keeps
/// pointing home. Off by default, and it needs a URL before it can be switched on.
///
/// Every 15 minutes it asks the same Cloudflare server the speed test uses which address this network is at (one request, for a file of no
/// bytes), and if that isn't the address it last updated successfully, it calls your URL. Updating is by what was last <i>accepted</i>, not by
/// what changed: a refused or unreachable update is tried again 15 minutes later, and switching it on updates once even if nothing has moved.
/// Three failures in a row raise one alert. The URL often carries a token or password, so it is never shown again once saved, and a
/// <c>user:password@</c> in it is sent as a Basic login rather than left in the address.
/// </summary>
public sealed class DdnsUpdater : BackgroundService
{
    public sealed record Result(string At, string Ip, bool Ok, int? Status, string? Reply, string? Error);

    public static readonly TimeSpan Every = TimeSpan.FromMinutes(15);
    public const int FailuresBeforeAlert = 3;

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<DdnsUpdater> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);

    /// <summary>How the address is asked for. Swapped out by tests.</summary>
    internal Func<CancellationToken, Task<(string? Ip, string? Error)>>? Lookup { get; set; }
    /// <summary>Sends the request and returns the status and the start of the body. Swapped out by tests.</summary>
    internal Func<HttpRequestMessage, CancellationToken, Task<(int Status, string Body)>>? Send { get; set; }

    public DdnsUpdater(HostStore store, ScannerService scanner, IHttpClientFactory http, ILogger<DdnsUpdater> log)
    {
        _store = store; _scanner = scanner; _http = http; _log = log;
    }

    public string Url => (_store.GetSetting("ddnsUrl") ?? "").Trim();
    public bool HasUrl => Url != "";
    public bool Enabled => _store.GetSetting("ddnsEnabled") == "true" && HasUrl;
    public string? PushedIp => _store.GetSetting("ddnsPushed") is { Length: > 0 } p ? p : null;

    public Result? Last
    {
        get
        {
            try { return JsonSerializer.Deserialize<Result>(_store.GetSetting("ddnsResult") ?? "null"); }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>What to show of the saved URL: where it goes, never the token or password in it.</summary>
    public static string Mask(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        return $"{u.Scheme}://{u.Host}{(u.IsDefaultPort ? "" : ":" + u.Port)}" + (u.PathAndQuery.Length > 1 ? "/…" : "");
    }

    /// <summary>An error to show for a URL that can't be used, or null.</summary>
    public static string? Validate(string url)
    {
        if (url.Length > 500) return "That URL is implausibly long.";
        if (!Uri.TryCreate(url.Replace("{ip}", "203.0.113.9"), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            return "Enter a full http:// or https:// update URL.";
        return null;
    }

    /// <summary>The request for an update: <c>{ip}</c> filled in, and a login written into the address sent as a Basic header instead.</summary>
    public static HttpRequestMessage Build(string url, string ip)
    {
        var filled = url.Replace("{ip}", ip);
        var u = new Uri(filled);
        var req = new HttpRequestMessage(HttpMethod.Get, new UriBuilder(u) { UserName = "", Password = "" }.Uri);
        if (u.UserInfo != "")
        {
            var parts = u.UserInfo.Split(':', 2);
            var login = WebUtility.UrlDecode(parts[0]) + ":" + WebUtility.UrlDecode(parts.Length > 1 ? parts[1] : "");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(login)));
        }
        req.Headers.UserAgent.ParseAdd("BAMF-Network-Monitor");
        return req;
    }

    /// <summary>Several providers answer 200 and say no in the body ("badauth", "nohost", "abuse").</summary>
    public static bool Refused(string body)
    {
        var first = body.Trim().ToLowerInvariant().Split(' ', '\r', '\n', '\t')[0];
        return first is "badauth" or "badagent" or "badsys" or "bad" or "nohost" or "notfqdn" or "abuse" or "911" or "dnserr" or "!yrel" or "!donator" or "ko" or "error";
    }

    /// <summary>
    /// Learns the address and, if it isn't the one last updated (or <paramref name="force"/>), calls the URL. Returns the outcome, or null
    /// when there was nothing to do or an update was already running.
    /// </summary>
    public async Task<Result?> Run(bool force, CancellationToken ct)
    {
        if (!HasUrl) return null;
        if (!await _busy.WaitAsync(0, ct)) return null;
        try
        {
            var (found, error) = Lookup is { } l ? await l(ct) : await ExternalIpLookup.Find(_http.CreateClient(), ct);
            if (error is null) _store.RecordExternalIp(found, "ddns");
            var ip = _store.GetExternalIp()?.Ip;
            var now = DateTime.UtcNow.ToString("o");
            if (ip is null)
            {
                var r = new Result(now, "", false, null, null, "Couldn't learn this network's public address" + (error is null ? "." : ": " + error));
                _store.SetSetting("ddnsResult", JsonSerializer.Serialize(r));
                return r;
            }
            if (!force && ip == PushedIp) return null;

            Result result;
            try
            {
                using var req = Build(Url, ip);
                (int Status, string Body) reply;
                if (Send is { } s) reply = await s(req, ct);
                else
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(20));
                    using var resp = await _http.CreateClient().SendAsync(req, cts.Token);
                    var text = await resp.Content.ReadAsStringAsync(cts.Token);
                    reply = ((int)resp.StatusCode, text.Length > 200 ? text[..200] : text);
                }
                var ok = reply.Status is >= 200 and < 300 && !Refused(reply.Body);
                var said = reply.Body.Trim();
                result = new Result(now, ip, ok, reply.Status, said == "" ? null : said,
                    ok ? null : reply.Status is >= 200 and < 300 ? "The provider refused it: " + said : $"The provider answered HTTP {reply.Status}.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                result = new Result(now, ip, false, null, null, ex.GetBaseException().Message);
            }
            _store.SetSetting("ddnsResult", JsonSerializer.Serialize(result));
            await Settle(result, force, ct);
            return result;
        }
        finally { _busy.Release(); }
    }

    /// <summary>Remembers what was accepted, counts failures in a row, and says so once when they reach the limit.</summary>
    private async Task Settle(Result r, bool manual, CancellationToken ct)
    {
        if (r.Ok)
        {
            _store.SetSetting("ddnsPushed", r.Ip);
            _store.SetSetting("ddnsFailures", "0");
            _log.LogInformation("Dynamic DNS updated to {Ip}", r.Ip);
            return;
        }
        _log.LogWarning("Dynamic DNS update failed: {Error}", r.Error);
        if (manual) return;
        var n = (int.TryParse(_store.GetSetting("ddnsFailures"), out var f) ? f : 0) + 1;
        _store.SetSetting("ddnsFailures", n.ToString());
        if (n != FailuresBeforeAlert) return;
        var title = "The dynamic DNS update keeps failing";
        var detail = $"{n} updates in a row to {Mask(Url)} for {r.Ip} haven't been accepted: {r.Error} The name it keeps may still point at the old address. BAMF tries again every 15 minutes.";
        _store.AddAlert("ddns", title, detail);
        await _scanner.SendGenericAlert(title, detail, "internet", ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { if (Enabled) await Run(false, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "The dynamic DNS updater failed"); }
            try { await Task.Delay(Every, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
