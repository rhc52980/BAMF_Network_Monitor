using System.Net.Http.Headers;
using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// Optional, opt-in check for a newer release on GitHub. Off by default: BAMF
/// frequently runs on isolated networks, and "makes no outbound calls you
/// didn't configure" is worth keeping true unless the operator says otherwise.
///
/// It only ever reads. Nothing is downloaded and nothing is installed - the
/// dashboard just shows a badge linking to the release page.
/// </summary>
public class UpdateChecker
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IHttpClientFactory _http;
    private readonly HostStore _store;
    private readonly IConfiguration _config;
    private readonly ILogger<UpdateChecker> _log;
    private readonly string _currentVersion;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool Enabled =>
        _store.GetSetting("updateCheck") is { } s
            ? s == "true"
            : _config.GetValue("Bamf:UpdateCheck", false);

    public string? LatestVersion { get; private set; }
    public string? ReleaseUrl { get; private set; }
    public DateTime? LastCheckedUtc { get; private set; }
    public bool UpdateAvailable { get; private set; }

    public UpdateChecker(IHttpClientFactory http, HostStore store, IConfiguration config,
                         ILogger<UpdateChecker> log, string currentVersion)
    {
        _http = http;
        _store = store;
        _config = config;
        _log = log;
        _currentVersion = currentVersion;
    }

    /// <summary>
    /// Checks at most once per day. Any failure - no internet, rate limit,
    /// private repo, malformed response - is swallowed: an update check must
    /// never disturb scanning.
    /// </summary>
    public async Task MaybeCheckAsync(CancellationToken ct)
    {
        if (!Enabled) return;
        if (LastCheckedUtc is { } last && DateTime.UtcNow - last < Interval) return;
        if (!await _gate.WaitAsync(0, ct)) return;

        try
        {
            LastCheckedUtc = DateTime.UtcNow;
            var repo = _config["Bamf:UpdateRepo"] ?? "rhc52980/BAMF_Network_Monitor";

            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            // GitHub rejects requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BAMF", _currentVersion));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var resp = await client.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
            if (!resp.IsSuccessStatusCode)
            {
                // 404 is the normal answer for a private repo or a repo with no
                // releases yet - not worth shouting about.
                _log.LogDebug("Update check returned {Status}", (int)resp.StatusCode);
                return;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) return;

            LatestVersion = tag.TrimStart('v', 'V');
            ReleaseUrl = url;
            UpdateAvailable = IsNewer(LatestVersion, _currentVersion);

            if (UpdateAvailable)
                _log.LogInformation("Update available: {Latest} (running {Current})", LatestVersion, _currentVersion);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Update check failed");
        }
        finally { _gate.Release(); }
    }

    /// <summary>True when <paramref name="candidate"/> is a higher version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (!Version.TryParse(Normalise(candidate), out var a)) return false;
        if (!Version.TryParse(Normalise(current), out var b)) return false;
        return a > b;
    }

    // "v1.3" and "1.3.0+abc123" both need to become something Version can parse.
    private static string Normalise(string? v)
    {
        var s = (v ?? "").Trim().TrimStart('v', 'V').Split('+')[0].Split('-')[0];
        var parts = s.Split('.');
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0",
            2 => $"{parts[0]}.{parts[1]}.0",
            _ => s,
        };
    }
}
