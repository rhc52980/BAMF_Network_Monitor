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
    private readonly SpeedTest _speed;
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

    // Quality: which problems (loss, jitter, dns) are being reported now, and when each was last raised.
    private readonly HashSet<string> _qualityActive = new();
    private readonly Dictionary<string, DateTime> _qualitySaid = new();
    public static readonly TimeSpan QualityWindow = TimeSpan.FromMinutes(15);
    /// <summary>Times one DNS lookup through the system's resolver, in ms (-1 failed, -2 nothing to ask). Swapped out by tests.</summary>
    internal Func<CancellationToken, Task<int>>? DnsTimer { get; set; }

    public WanWatch(HostStore store, ScannerService scanner, SpeedTest speed, IConfiguration cfg, ILogger<WanWatch> log)
    {
        _store = store; _scanner = scanner; _speed = speed; _cfg = cfg; _log = log;
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

    /// <summary>Whether lost pings, jitter and slow DNS are alerted on. On unless switched off; nothing without the watch itself.</summary>
    public bool QualityEnabled => _store.GetSetting("wanQuality") != "false";

    /// <summary>The last hour's quality, for the Internet card.</summary>
    public WanQuality.Report Quality(int minutes = 60)
    {
        var from = DateTime.UtcNow.AddMinutes(-minutes).ToString("o");
        return WanQuality.Assess(_store.GetWanSamples(Math.Max(1, (minutes + 59) / 60)).Where(s => string.CompareOrdinal(s.At, from) >= 0).Select(s => (s.Internet, s.Dns)).ToList());
    }

    /// <summary>The problems being reported now: "loss", "jitter", "dns".</summary>
    public IReadOnlyCollection<string> QualityProblems => _qualityActive.ToList();

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
        // A speed test fills the line on purpose. A ping taken then would
        // count as slow, or as lost, so that minute goes unwatched instead.
        if (_speed.LineBusy) return;
        var gateway = PortChecker.DefaultGateways().FirstOrDefault();
        var gwMs = gateway is null ? -1 : await PingMs(gateway, ct);
        var netMs = await PingMs(Target, ct);
        var dnsMs = await (DnsTimer ?? DnsLookupMs)(ct);
        _store.AddWanSample(gwMs, netMs, dnsMs);

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
                    $"{Target} has missed three pings in a row. {where} BAMF will say when it comes back.", ct, "internet");
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
                    $"from {_downSince.ToLocalTime():HH:mm} to {ended.ToLocalTime():HH:mm}.{local}", ct, "internet");
            }
            _wasDown = false;
            _misses = 0;
        }

        await CheckSlow(gwMs, netMs, ct);
        await CheckQuality(ct);
    }

    /// <summary>One A lookup of dns.google through the first DNS server this machine is set to use, timed.</summary>
    private static async Task<int> DnsLookupMs(CancellationToken ct)
    {
        var server = DnsClient.SystemServers().FirstOrDefault();
        if (server is null) return -2;
        var a = await DnsClient.QueryA(server, "dns.google", 2000, ct);
        return a.Ok && a.RCode == 0 ? a.Ms : -1;
    }

    /// <summary>
    /// The last quarter hour's readings: pings lost (the line not being down), the time varying from one ping to the
    /// next, and DNS lookups slow or failing. Each problem is one alert when it starts and one when the window is clean
    /// again, and the same problem isn't raised again within six hours. Nothing is said while the line is down.
    /// </summary>
    internal async Task CheckQuality(CancellationToken ct, DateTime? nowUtc = null)
    {
        if (!QualityEnabled || _wasDown) { return; }
        var now = nowUtc ?? DateTime.UtcNow;
        var from = now.Subtract(QualityWindow).ToString("o");
        var window = _store.GetWanSamples(1).Where(s => string.CompareOrdinal(s.At, from) >= 0).Select(s => (s.Internet, s.Dns)).ToList();
        var report = WanQuality.Assess(window);
        if (report.Samples < WanQuality.MinSamples) return;
        var problems = WanQuality.Problems(report);
        var mins = (int)QualityWindow.TotalMinutes;
        foreach (var p in problems.Where(p => !_qualityActive.Contains(p)).ToList())
        {
            _qualityActive.Add(p);
            if (_qualitySaid.TryGetValue(p, out var at) && now - at < TimeSpan.FromHours(6)) continue;
            _qualitySaid[p] = now;
            var (title, detail) = p switch
            {
                "loss" => ("The internet is dropping packets",
                    $"{report.LossPercent}% of the pings to {Target} in the last {mins} minutes got no answer, though the line is up. Pages still load, but calls break up, games lag and downloads stall. Usually the line or the modem: a noisy cable connection, a poor Wi-Fi link if BAMF's machine is on Wi-Fi, or your provider having a bad afternoon."),
                "jitter" => ("The internet is jittery",
                    $"The ping to {Target} has been varying by about {report.JitterMs} ms from one reading to the next over the last {mins} minutes. The average can look fine while calls stutter and games rubber-band. Something is filling the connection in bursts (a backup, a cloud upload, a camera) or the line itself is unsteady."),
                _ => ("DNS lookups are slow",
                    report.DnsMs is { } d && d >= WanQuality.DnsSlowLimit
                        ? $"Looking a name up through this network's DNS has been taking about {d} ms over the last {mins} minutes. Every page starts with a lookup, so everything feels slow to begin even when the speed test is fine. The resolver is usually the router passing the question to your provider: a public one such as 1.1.1.1 or 9.9.9.9 is worth trying."
                        : $"{report.DnsFailed} of the last {report.DnsMeasured} DNS lookups through this network's resolver got no answer. When the resolver fails, pages fail to open even though the line is up. The resolver is usually the router passing the question to your provider: a public one such as 1.1.1.1 or 9.9.9.9 is worth trying."),
            };
            await _scanner.RaiseSecurity(title, detail, ct, "internet");
        }
        foreach (var p in _qualityActive.Where(p => !problems.Contains(p)).ToList())
        {
            _qualityActive.Remove(p);
            var (title, detail) = p switch
            {
                "loss" => ("The internet has stopped dropping packets", $"Pings to {Target} are being answered again: {report.LossPercent}% lost in the last {mins} minutes."),
                "jitter" => ("The internet is steady again", $"The ping to {Target} varies by about {report.JitterMs} ms again over the last {mins} minutes."),
                _ => ("DNS lookups are quick again", $"Lookups through this network's DNS are taking about {report.DnsMs} ms again."),
            };
            await _scanner.RaiseSecurity(title, detail, ct, "internet");
        }
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
                $"{Target} has been taking about {mid} ms to answer{usual}.{lossText}{where} BAMF will say when it's back to normal.", ct, "internet");
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
            $"from {since.ToLocalTime():HH:mm} to {ended.ToLocalTime():HH:mm}, at worst {worst} ms.", ct, "internet");
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
