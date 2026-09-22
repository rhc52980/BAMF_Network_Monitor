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
    public sealed record State(bool Enabled, string Target, bool? Up, bool? RouterUp, int? Ms, string? Since, string? CheckedAt,
        string SlowMode, int? SlowMs, int? UsualMs, bool Slow, string? SlowSince);

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly IConfiguration _cfg;
    private readonly ILogger<WanWatch> _log;
    private bool _wasDown;
    private bool _downLocal;
    private DateTime _downSince;
    private int _misses;
    private DateTime _lastPrune = DateTime.MinValue;

    // Slowness: the last few readings, and the spell in progress if there is one.
    private readonly Queue<(int Gw, int Net)> _recent = new();
    private bool _wasSlow;
    private DateTime _slowSince;
    private int _slowWorst, _slowUsual, _fastRun;
    private bool _slowLocal;
    private int? _usual;
    private DateTime _usualAt = DateTime.MinValue;

    /// <summary>Readings in a row that must all be slow before it's called, and fast ones before it's over.</summary>
    public const int SlowReadings = 5, FastReadings = 3;

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

    /// <summary>
    /// How slow counts as slow: "auto" (the default) is four times the usual
    /// ping and at least 100 ms more than it; a number is a fixed limit in ms;
    /// "off" doesn't watch for it.
    /// </summary>
    public string SlowMode
    {
        get
        {
            var v = (_store.GetSetting("wanSlow") ?? _cfg["Bamf:WanSlow"] ?? "auto").Trim().ToLowerInvariant();
            return v == "off" || v == "auto" || int.TryParse(v, out var n) && n is >= 20 and <= 5000 ? v : "auto";
        }
    }

    /// <summary>The usual ping to the far address: the median of the last day's answers, once there are enough.</summary>
    public int? UsualMs
    {
        get
        {
            // Held still during a slow spell, or a long one would become the new usual.
            if (_wasSlow || DateTime.UtcNow - _usualAt < TimeSpan.FromMinutes(10)) return _usual;
            var ms = _store.GetWanSamples(24).Where(x => x.Internet >= 0).Select(x => x.Internet).OrderBy(x => x).ToList();
            _usual = ms.Count >= 30 ? ms[ms.Count / 2] : null;
            _usualAt = DateTime.UtcNow;
            return _usual;
        }
    }

    /// <summary>Forgets the usual, so the next reading works it out again.</summary>
    public void ResetUsual() => _usualAt = DateTime.MinValue;

    /// <summary>The limit in ms a reading has to reach to count as slow, or null when there isn't one yet.</summary>
    public int? SlowLimit
    {
        get
        {
            var mode = SlowMode;
            if (mode == "off") return null;
            if (int.TryParse(mode, out var fixedMs)) return fixedMs;
            return UsualMs is { } u ? Math.Max(u * 4, u + 100) : null;
        }
    }

    /// <summary>The slow spell in progress, if there is one.</summary>
    public (DateTime Since, int Worst, int Usual, bool Local)? CurrentSlow => _wasSlow ? (_slowSince, _slowWorst, _slowUsual, _slowLocal) : null;

    /// <summary>The outage in progress, if there is one.</summary>
    public (DateTime Since, bool Local)? Current => _wasDown ? (_downSince, _downLocal) : null;

    public State Now()
    {
        var last = _store.GetWanSamples(6).LastOrDefault();
        var mode = SlowMode;
        if (!Enabled || last is null) return new State(Enabled, Target, null, null, null, null, last?.At, mode, null, null, false, null);
        var up = last.Internet >= 0;
        return new State(Enabled, Target, up, last.Gateway >= 0, up ? last.Internet : null,
            _wasDown ? _downSince.ToString("o") : null, last.At,
            mode, SlowLimit, UsualMs, _wasSlow, _wasSlow ? _slowSince.ToString("o") : null);
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

        await CheckSlow(gwMs, netMs, ct);
    }

    /// <summary>
    /// Up but crawling. Five readings in a row at or over the limit, most of
    /// them answered, and it's slow; three answered under it and it's over. A
    /// lost ping counts as slow here, since losing some is part of a bad line,
    /// but it takes the outage's three in a row to be called down. While the
    /// line is down there is nothing to time, so a spell running then just ends.
    /// </summary>
    private async Task CheckSlow(int gwMs, int netMs, CancellationToken ct)
    {
        var limit = SlowLimit;
        if (_wasDown || limit is null)
        {
            if (_wasSlow) EndSlow(DateTime.UtcNow);
            _recent.Clear(); _fastRun = 0;
            return;
        }
        _recent.Enqueue((gwMs, netMs));
        while (_recent.Count > SlowReadings) _recent.Dequeue();
        var slow = netMs < 0 || netMs >= limit;

        if (!_wasSlow)
        {
            if (_recent.Count < SlowReadings) return;
            var answered = _recent.Where(r => r.Net >= 0).Select(r => r.Net).OrderBy(x => x).ToList();
            if (!_recent.All(r => r.Net < 0 || r.Net >= limit) || answered.Count < 3) return;
            _slowUsual = UsualMs ?? 0;
            _wasSlow = true;
            _fastRun = 0;
            _slowSince = DateTime.UtcNow.AddSeconds(-(SlowReadings - 1) * IntervalSeconds);
            _slowWorst = answered[^1];
            // The router is on this side of the line; a slow answer from it too
            // means the delay is in here, not out there.
            var gw = _recent.Select(r => r.Gw).OrderBy(x => x).ToList();
            var gwMid = gw[gw.Count / 2];
            _slowLocal = gwMid < 0 || gwMid >= 50;
            var mid = answered[answered.Count / 2];
            var lost = _recent.Count(r => r.Net < 0);
            var usual = _slowUsual > 0 ? $", against a usual {_slowUsual} ms" : "";
            var lossText = lost > 0 ? $" {lost} of the last {SlowReadings} pings got no answer at all." : "";
            var where = _slowLocal
                ? " Your router is slow to answer too (" + (gwMid < 0 ? "it's missing pings" : gwMid + " ms") + "), so the delay is in here: something may be filling the connection, or the router is struggling."
                : " Your router answers in " + (gwMid == 0 ? "under a millisecond" : gwMid + " ms") + $", so the delay is beyond it: the line, the modem or your provider.";
            await _scanner.RaiseSecurity("The internet is slow",
                $"{Target} has been taking about {mid} ms to answer{usual}.{lossText}{where} BAMF will say when it's back to normal.", ct);
            return;
        }

        if (netMs >= 0 && netMs > _slowWorst) _slowWorst = netMs;
        _fastRun = slow ? 0 : _fastRun + 1;
        if (_fastRun < FastReadings) return;
        // Over since the first of the fast readings.
        var ended = DateTime.UtcNow.AddSeconds(-(FastReadings - 1) * IntervalSeconds);
        var since = _slowSince;
        var worst = _slowWorst;
        EndSlow(ended);
        var mins = Math.Max(1, (int)Math.Round((ended - since).TotalMinutes));
        await _scanner.RaiseSecurity("The internet is back to normal",
            $"{Target} is answering in {netMs} ms again. It was slow for about {mins} minute{(mins == 1 ? "" : "s")}, " +
            $"from {since.ToLocalTime():HH:mm} to {ended.ToLocalTime():HH:mm}, at worst {worst} ms.", ct);
    }

    /// <summary>Writes the spell down and forgets it. Written before any alert, so the history is there regardless.</summary>
    private void EndSlow(DateTime ended)
    {
        if (!_wasSlow) return;
        if (ended < _slowSince) ended = _slowSince;
        _store.AddWanSlow(_slowSince, ended, _slowWorst, _slowUsual, _slowLocal);
        _wasSlow = false;
        _fastRun = 0;
        _recent.Clear();
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
