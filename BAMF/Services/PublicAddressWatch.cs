namespace LanWatch.Services;

/// <summary>
/// An alert when the address the internet sees this network at changes. The modem rebooted, or the provider moved you: either way,
/// a port forward, a VPN back home or a dynamic DNS name pointing here is wrong until it catches up, and this is the alert that says why.
///
/// Off by default, because it's the one thing that looks outward on a timer: every 15 minutes, one request to the same Cloudflare
/// server the speed test uses, for a file of no bytes, whose answer says which address the request came from. What it learns goes in the
/// same place as the speed test's and GreyNoise's, so a change either of those notices is announced too. The first address learned is
/// only remembered; and switching the watch on takes today's address as the starting point rather than announcing old changes.
/// </summary>
public sealed class PublicAddressWatch : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(15);

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<PublicAddressWatch> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);

    /// <summary>How the address is asked for: the address, or why not. Swapped out by tests.</summary>
    internal Func<CancellationToken, Task<(string? Ip, string? Error)>>? Lookup { get; set; }

    public PublicAddressWatch(HostStore store, ScannerService scanner, IHttpClientFactory http, ILogger<PublicAddressWatch> log)
    {
        _store = store; _scanner = scanner; _http = http; _log = log;
    }

    public bool Enabled => _store.GetSetting("addressWatch") == "true";

    /// <summary>Switching on: today's address is where it starts from, so changes from before aren't announced.</summary>
    public void Baseline() => _store.SetSetting("addressAlertedAt", _store.GetExternalIp()?.ChangedAt ?? "");

    /// <summary>Asks for the address now, records it, and says so if it has changed since the last announcement. Null if one was already running.</summary>
    public async Task<string?> Check(CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct)) return null;
        try
        {
            var (ip, error) = Lookup is { } l ? await l(ct) : await ExternalIpLookup.Find(_http.CreateClient(), ct);
            if (error is not null) { _log.LogDebug("Public address lookup: {Error}", error); }
            else _store.RecordExternalIp(ip, "watch");
            await Announce(ct);
            return error;
        }
        finally { _busy.Release(); }
    }

    private async Task Announce(CancellationToken ct)
    {
        var now = _store.GetExternalIp();
        if (now?.ChangedAt is not { Length: > 0 } changedAt || changedAt == _store.GetSetting("addressAlertedAt")) return;
        _store.SetSetting("addressAlertedAt", changedAt);
        var via = now.Source switch { "speedtest" => "the speed test", "greynoise" => "the GreyNoise check", "lookup" => "a lookup", _ => "this watch" };
        await _scanner.RaiseSecurity("Your public IP address changed",
            $"The internet now sees this network at {now.Ip}; it was {now.Previous}. Usually the modem restarted or your provider moved you (found by {via}). " +
            "Anything that reaches this network by its address, such as a port forward, a VPN back home or a dynamic DNS name, needs the new one.", ct, "internet");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { if (Enabled) await Check(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "The public address watch failed"); }
            try { await Task.Delay(Every, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
