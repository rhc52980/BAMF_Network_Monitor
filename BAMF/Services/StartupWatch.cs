namespace LanWatch.Services;

/// <summary>
/// Says so when BAMF comes back after stopping without being asked to. The heartbeat covers BAMF going quiet; this covers it
/// having been quiet and no one noticing: after a power cut, a crash or the machine being switched off BAMF starts again on its
/// own, and nothing said it had been blind for twelve minutes. A normal stop (the service stopped, an update, a restart you chose)
/// is told apart from that, so updates don't alert.
///
/// How: every minute BAMF writes down that it is alive, and a clean shutdown writes down that it was clean. At the next start,
/// a last "alive" with no clean stop after it means it was cut off.
/// </summary>
public sealed class StartupWatch : BackgroundService
{
    private const string AliveKey = "aliveAt", CleanKey = "cleanStop";

    private readonly HostStore _store;
    private readonly ScannerService? _scanner;
    private readonly ILogger<StartupWatch> _log;
    private DateTime? _cutOffAt;

    public StartupWatch(HostStore store, ScannerService? scanner, ILogger<StartupWatch> log, IHostApplicationLifetime? lifetime = null)
    {
        _store = store; _scanner = scanner; _log = log;
        // The first sign of a normal stop, before the hosted services are asked to stop: a stop signal that gives the
        // process only a few seconds still gets this written down.
        lifetime?.ApplicationStopping.Register(() =>
        {
            try { Stopped(DateTime.UtcNow); }
            catch (Exception ex) { _log.LogWarning(ex, "Couldn't note that BAMF stopped normally"); }
        });
    }

    /// <summary>On unless switched off in Settings.</summary>
    public bool Enabled => _store.GetSetting("restartAlert") != "false";

    /// <summary>
    /// Called once as BAMF starts: returns when it was last known to be running if it was cut off, else null, and
    /// writes down that it is running now and not yet stopped cleanly.
    /// </summary>
    internal DateTime? Begin(DateTime nowUtc)
    {
        DateTime? cutOff = null;
        if (DateTime.TryParse(_store.GetSetting(AliveKey), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var last)
            && _store.GetSetting(CleanKey) != "true")
            cutOff = last.ToUniversalTime();
        _store.SetSetting(CleanKey, "false");
        _store.SetSetting(AliveKey, nowUtc.ToString("o"));
        return cutOff;
    }

    internal void Alive(DateTime nowUtc) => _store.SetSetting(AliveKey, nowUtc.ToString("o"));

    /// <summary>A normal stop: the next start won't take it for a cut-off.</summary>
    internal void Stopped(DateTime nowUtc)
    {
        _store.SetSetting(AliveKey, nowUtc.ToString("o"));
        _store.SetSetting(CleanKey, "true");
    }

    /// <summary>What the alert says, for a BAMF cut off at <paramref name="cutOffUtc"/> and back at <paramref name="nowUtc"/>.</summary>
    internal static (string Title, string Detail) Message(DateTime cutOffUtc, DateTime nowUtc)
    {
        var mins = Math.Max(1, (int)Math.Round((nowUtc - cutOffUtc).TotalMinutes));
        return ("BAMF started again after stopping unexpectedly",
            $"BAMF last said it was running at {cutOffUtc.ToLocalTime():HH:mm} and started again at {nowUtc.ToLocalTime():HH:mm}: about {mins} minute{(mins == 1 ? "" : "s")} " +
            "with nothing watching. A power cut, a crash or the machine being switched off does this; stopping the service or updating doesn't. " +
            "Anything that changed in between is noticed at the first scan.");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { _cutOffAt = Begin(DateTime.UtcNow); }
        catch (Exception ex) { _log.LogWarning(ex, "Couldn't read when BAMF was last running"); }
        try
        {
            // Let the first scan and the destinations settle before saying anything.
            if (_cutOffAt is { } cut)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                if (Enabled && _scanner is not null)
                {
                    var (title, detail) = Message(cut, DateTime.UtcNow);
                    await _scanner.RaiseSecurity(title, detail, ct);
                }
                _cutOffAt = null;
            }
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                Alive(DateTime.UtcNow);
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
        catch (Exception ex) { _log.LogWarning(ex, "The restart watch failed"); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try { Stopped(DateTime.UtcNow); }
        catch (Exception ex) { _log.LogWarning(ex, "Couldn't note that BAMF stopped normally"); }
    }
}
