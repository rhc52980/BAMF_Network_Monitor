namespace LanWatch.Services;

/// <summary>
/// A heartbeat out to a "dead man's switch" service (Healthchecks.io, Uptime Kuma's push monitors, or any address
/// that expects a visit): BAMF asks for the address you give it every few minutes, and the service tells you when
/// the visits stop. It's how you hear that BAMF, or the machine it runs on, has gone quiet, the one thing BAMF can't
/// tell you itself. Off by default. The address is a credential (the secret is usually in its path), so it's saved
/// like a webhook, never sent back to the dashboard in full and never put in a settings export.
/// </summary>
public sealed class Heartbeat : BackgroundService
{
    public const int MinMinutes = 1, MaxMinutes = 1440, DefaultMinutes = 5;

    private readonly HostStore _store;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<Heartbeat> _log;
    private readonly object _lock = new();
    private DateTime? _lastAttempt;
    private string? _lastOk, _lastError;
    private string? _lastAt;
    private CancellationTokenSource _wake = new();

    public Heartbeat(HostStore store, IHttpClientFactory http, ILogger<Heartbeat> log) { _store = store; _http = http; _log = log; }

    public bool Enabled => _store.GetSetting("heartbeatEnabled") == "true";
    public string? Url => _store.GetSetting("heartbeatUrl") is { Length: > 0 } u ? u : null;
    public int Minutes => int.TryParse(_store.GetSetting("heartbeatMinutes"), out var m) && m is >= MinMinutes and <= MaxMinutes ? m : DefaultMinutes;

    /// <summary>On, with an address: it is visiting.</summary>
    public bool Active => Enabled && Url is not null;

    public sealed record State(string? At, bool? Ok, string? Error);
    public State Last { get { lock (_lock) return new State(_lastAt, _lastError is not null ? false : _lastOk is not null ? true : null, _lastError); } }

    /// <summary>An address BAMF will visit: a full web address. Returns an error, or null.</summary>
    public static string? Check(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Give it the address your service shows, like https://hc-ping.com/…";
        if (url.Length > 500) return "That address is longer than any BAMF would save.";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || string.IsNullOrEmpty(u.Host))
            return "That isn't a web address. It starts with http:// or https://.";
        return null;
    }

    /// <summary>What may be shown of an address: where it goes, never the path or query that carries the secret.</summary>
    public static string? Masked(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        return $"{u.Scheme}://{u.Authority}/…";
    }

    /// <summary>The settings changed: act on them now rather than at the next look.</summary>
    public void Reconfigure() { try { _wake.Cancel(); } catch (ObjectDisposedException) { } lock (_lock) _lastAttempt = null; }

    /// <summary>Visits the address once, now, and says how it went. Used by the test button and the loop.</summary>
    public async Task<State> VisitNow(CancellationToken ct)
    {
        var url = Url;
        if (url is null) return new State(null, false, "No address saved.");
        string? error = null;
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("BAMF");
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) error = $"It answered HTTP {(int)resp.StatusCode}.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { error = ex is TaskCanceledException ? "It didn't answer in time." : "Couldn't reach it: " + ex.GetBaseException().Message; }
        var now = DateTime.UtcNow.ToString("o");
        lock (_lock)
        {
            _lastAttempt = DateTime.UtcNow; _lastAt = now; _lastError = error; _lastOk = error is null ? now : _lastOk;
        }
        if (error is not null) _log.LogWarning("Heartbeat: {Error}", error);
        return new State(now, error is null, error);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool due;
                lock (_lock) due = _lastAttempt is null || DateTime.UtcNow - _lastAttempt >= TimeSpan.FromMinutes(Minutes);
                if (Active && due) await VisitNow(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Heartbeat failed"); }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _wake.Token);
            try { await Task.Delay(TimeSpan.FromSeconds(15), linked.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (OperationCanceledException) { break; }
            if (_wake.IsCancellationRequested) _wake = new CancellationTokenSource();
        }
    }
}
