namespace LanWatch.Services;

/// <summary>
/// Asks the internet what address a request from here comes from, on demand: the Network services card's
/// "Look it up" button. It's one request to the same Cloudflare server the speed test uses, for a file of no
/// bytes, and Cloudflare says in its answer which address it came from. Nothing runs on a timer.
/// </summary>
public static class ExternalIpLookup
{
    public const string Url = "https://speed.cloudflare.com/__down?bytes=0";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The address Cloudflare says the request came from, or null if it didn't say.</summary>
    internal static string? FromResponse(HttpResponseMessage r) =>
        r.Headers.TryGetValues("cf-meta-ip", out var v) ? v.FirstOrDefault()?.Trim() : null;

    /// <summary>The public address, or why it couldn't be found.</summary>
    public static async Task<(string? Ip, string? Error)> Find(HttpClient http, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(Timeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Url);
            req.Headers.UserAgent.ParseAdd("BAMF");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stop.Token);
            if (!resp.IsSuccessStatusCode) return (null, $"Cloudflare answered HTTP {(int)resp.StatusCode}.");
            var ip = FromResponse(resp);
            return string.IsNullOrEmpty(ip) ? (null, "Cloudflare didn't say which address it saw.") : (ip, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, "Cloudflare didn't answer in time."); }
        catch (HttpRequestException ex) { return (null, "Couldn't reach Cloudflare: " + ex.GetBaseException().Message); }
    }
}
