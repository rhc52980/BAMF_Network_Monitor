using System.Net.NetworkInformation;

namespace LanWatch.Services;

/// <summary>
/// Is the internet down, or just this house? Once a minute this pings the
/// router and one address out on the internet, and keeps both answers. When
/// the far address stops answering it says so, and says whether the router
/// answered too - which is the difference between "your provider is down" and
/// "something in here is".
///
/// Off unless switched on, because the outside ping is a packet leaving the
/// house every minute, to an address you choose (8.8.8.8 by default, one of
/// Google's public DNS servers). Nothing about your network is sent: it's an
/// echo request, the same as any ping.
/// </summary>
public sealed class WanWatch : BackgroundService
{
    public sealed record State(bool Enabled, string Target, bool? Up, bool? RouterUp, int? Ms, string? Since, string? CheckedAt);

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly IConfiguration _cfg;
    private readonly ILogger<WanWatch> _log;
    private bool _wasDown;
    private bool _downLocal;
    private DateTime _downSince;
    private int _misses;
    private DateTime _lastPrune = DateTime.MinValue;

    public WanWatch(HostStore store, ScannerService scanner, IConfiguration cfg, ILogger<WanWatch> log)
    {
        _store = store; _scanner = scanner; _cfg = cfg; _log = log;
    }

    /// <summary>Saved in Settings wins; otherwise appsettings.json, which is off by default.</summary>
    public bool Enabled => _store.GetSetting("wanWatch") is { } s ? s == "true" : _cfg.GetValue("Bamf:WanWatch", false);

    /// <summary>How often it pings, from Settings, else appsettings.json.</summary>
    public int IntervalSeconds
    {
        get
        {
            var saved = _store.GetSetting("wanInterval");
            var v = int.TryParse(saved, out var s) ? s : _cfg.GetValue("Bamf:WanIntervalSeconds", 60);
            return Math.Clamp(v, 20, 3600);
        }
    }

    public string Target
    {
        get
        {
            var t = (_store.GetSetting("wanTarget") ?? _cfg["Bamf:WanTarget"] ?? "8.8.8.8").Trim();
            return System.Net.IPAddress.TryParse(t, out var ip) ? ip.ToString() : "8.8.8.8";
        }
    }

    /// <summary>The last reading, for the card on Activity.</summary>
    /// <summary>The outage in progress, if there is one.</summary>
    public (DateTime Since, bool Local)? Current => _wasDown ? (_downSince, _downLocal) : null;

    public State Now()
    {
        var last = _store.GetWanSamples(6).LastOrDefault();
        if (!Enabled || last is null) return new State(Enabled, Target, null, null, null, null, last?.At);
        var up = last.Internet >= 0;
        return new State(Enabled, Target, up, last.Gateway >= 0, up ? last.Internet : null,
            _wasDown ? _downSince.ToString("o") : null, last.At);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // A minute in, so a restart doesn't ping before the scanner has settled.
        await Task.Delay(TimeSpan.FromSeconds(45), ct).ContinueWith(_ => { }, ct);
        while (!ct.IsCancellationRequested)
        {
            var every = IntervalSeconds;
            try { if (Enabled) await Tick(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogDebug(ex, "Internet watch tick failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(every), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task Tick(CancellationToken ct)
    {
        var gateway = PortChecker.DefaultGateways().FirstOrDefault();
        var gwMs = gateway is null ? -1 : await PingMs(gateway, ct);
        var netMs = await PingMs(Target, ct);
        _store.AddWanSample(gwMs, netMs);

        if (DateTime.UtcNow - _lastPrune > TimeSpan.FromHours(6)) { _lastPrune = DateTime.UtcNow; _store.PruneWan(); }

        if (netMs < 0)
        {
            // Three in a row before saying anything: one lost ping is weather.
            _misses++;
            if (_misses == 3 && !_wasDown)
            {
                _wasDown = true;
                // It's been down since the first miss, two intervals ago.
                _downSince = DateTime.UtcNow.AddSeconds(-2 * IntervalSeconds);
                _downLocal = gwMs < 0;
                var where = gwMs >= 0
                    ? "Your router answered, so the line out of the house is the part that's down: your provider, the modem, or the cable to it."
                    : "Your router didn't answer either, so whatever's wrong is in here rather than out there.";
                await _scanner.RaiseSecurity("The internet is down",
                    $"{Target} has missed three pings in a row. {where} BAMF will say when it comes back.", ct);
            }
        }
        else
        {
            if (_wasDown)
            {
                // Written down before it's announced, so the history is there
                // even if the alert goes nowhere.
                var ended = DateTime.UtcNow;
                _store.AddWanOutage(_downSince, ended, _downLocal);
                var mins = Math.Max(1, (int)Math.Round((ended - _downSince).TotalMinutes));
                var local = _downLocal ? " Your router was down too, so it was something in here." : "";
                await _scanner.RaiseSecurity("The internet is back",
                    $"{Target} is answering again. It was down about {mins} minute{(mins == 1 ? "" : "s")}, " +
                    $"from {_downSince.ToLocalTime():HH:mm} to {ended.ToLocalTime():HH:mm}.{local}", ct);
            }
            _wasDown = false;
            _misses = 0;
        }
    }

    private static async Task<int> PingMs(string address, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, 2000);
            return reply.Status == IPStatus.Success ? (int)Math.Min(int.MaxValue, reply.RoundtripTime) : -1;
        }
        catch (Exception ex) when (ex is PingException or ArgumentException or InvalidOperationException) { return -1; }
    }
}
