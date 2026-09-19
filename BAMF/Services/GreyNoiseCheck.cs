using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LanWatch.Services;

/// <summary>
/// Has your home's public address been seen doing something bad on the
/// internet? GreyNoise runs sensors all over the internet and records every
/// address that scans them. If yours is one, something behind it has been
/// probing the internet: often a hacked camera, NAS or router in a botnet.
///
/// Off unless switched on in Settings, because it's a call out to two
/// services on the internet: one to learn this network's public address
/// (api.ipify.org, or checkip.amazonaws.com if that fails), and GreyNoise's
/// free lookup (api.greynoise.io, no account or key). Once a day, when on.
/// </summary>
public sealed class GreyNoiseCheck
{
    public sealed record Result(string CheckedAt, string? Ip, bool? Noise, bool? Riot, string? Classification,
        string? Name, string? LastSeen, string? Message, string? Error);

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<GreyNoiseCheck> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);

    public GreyNoiseCheck(HostStore store, ScannerService scanner, IHttpClientFactory http, ILogger<GreyNoiseCheck> log)
    {
        _store = store; _scanner = scanner; _http = http; _log = log;
    }

    public bool Enabled => _store.GetSetting("greynoise") == "true";

    public Result? Last
    {
        get
        {
            try { return JsonSerializer.Deserialize<Result>(_store.GetSetting("greynoiseResult") ?? "null"); }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>True when the last check is more than a day old, or there wasn't one.</summary>
    public bool Due => Enabled && (Last is not { } r || !DateTime.TryParse(r.CheckedAt, null,
        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
        || DateTime.UtcNow - at > TimeSpan.FromHours(24));

    public async Task<Result?> Check(CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct)) return Last;
        try
        {
            var previous = Last;
            var result = await Lookup(ct);
            _store.SetSetting("greynoiseResult", JsonSerializer.Serialize(result));
            _log.LogInformation("GreyNoise: {Ip} {Outcome}", result.Ip ?? "?", result.Error ?? (result.Noise == true ? "seen scanning" : "not seen scanning"));
            // One alert per sighting: a new address, or a newer last-seen date.
            if (result.Noise == true && !(previous?.Noise == true && previous.Ip == result.Ip && previous.LastSeen == result.LastSeen))
            {
                var what = result.Classification is { Length: > 0 } c && c != "unknown" ? $" GreyNoise classes it as {c}." : "";
                await _scanner.RaiseSecurity($"Your public address {result.Ip} has been seen scanning the internet",
                    $"GreyNoise's sensors recorded {result.Ip} probing the internet{(result.LastSeen is { Length: > 0 } ls ? ", last on " + ls : "")}.{what} " +
                    "Something behind it may be infected: often a camera, NAS or router in a botnet. Check the traffic monitor's top talkers for a device " +
                    "sending more than it should, and update or reset anything you don't recognise. Unless your internet provider shares one address " +
                    $"between many homes, in which case it may be a neighbour's. Details: https://viz.greynoise.io/ip/{result.Ip}", ct);
            }
            return result;
        }
        finally { _busy.Release(); }
    }

    private async Task<Result> Lookup(CancellationToken ct)
    {
        var now = DateTime.UtcNow.ToString("o");
        var http = _http.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BAMF");

        string? ip = null;
        foreach (var url in new[] { "https://api.ipify.org", "https://checkip.amazonaws.com" })
        {
            try
            {
                var text = (await http.GetStringAsync(url, ct)).Trim();
                if (IPAddress.TryParse(text, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) { ip = a.ToString(); break; }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        }
        if (ip is null) return new Result(now, null, null, null, null, null, null, null, "Couldn't learn this network's public address.");

        try
        {
            using var resp = await http.GetAsync($"https://api.greynoise.io/v3/community/{ip}", ct);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                return new Result(now, ip, null, null, null, null, null, null, "GreyNoise's free lookup is busy; it'll try again tomorrow.");
            var body = await resp.Content.ReadAsStringAsync(ct);
            var j = JsonNode.Parse(body);
            // 404 is GreyNoise's answer for an address it has never seen: clean.
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return new Result(now, ip, false, false, null, null, null, Str(j, "message") ?? "Not observed scanning the internet.", null);
            if (!resp.IsSuccessStatusCode)
                return new Result(now, ip, null, null, null, null, null, null, $"GreyNoise answered {(int)resp.StatusCode}.");
            return new Result(now, ip, Bool(j, "noise"), Bool(j, "riot"), Str(j, "classification"), Str(j, "name"),
                Str(j, "last_seen"), Str(j, "message"), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new Result(now, ip, null, null, null, null, null, null, "Couldn't reach GreyNoise: " + ex.GetBaseException().Message);
        }
    }

    private static string? Str(JsonNode? n, string k) { try { return n?[k]?.GetValue<string>(); } catch { return n?[k]?.ToString(); } }
    private static bool? Bool(JsonNode? n, string k) { try { return n?[k]?.GetValue<bool>(); } catch { return null; } }
}
