using System.Diagnostics;
using System.Net.Sockets;

namespace LanWatch.Services;

/// <summary>
/// Is the thing on the device working, not just the device? Every minute, each service you picked is checked: a web page (asked for, and a
/// normal answer is 200 to 399, redirects followed) or a port (does it accept a connection). The answers are kept for three days, so the
/// Activity card can draw the last 24 hours and say what share of checks passed.
///
/// An alert goes out when a service has failed two checks in a row, and another when it answers again. A service with a slow limit also
/// alerts when five answers in a row are over it, and when three in a row are back under. A device that BAMF has marked offline is not
/// asked about: the device's own alert says it, and a service on it can't be expected to answer. Devices that are snoozed or ignored stay quiet.
///
/// On by default, but it does nothing until a service is added. Only devices BAMF already knows can be watched.
/// </summary>
public sealed class ServiceWatch : BackgroundService
{
    public const int Max = 30, FailsToAlert = 2, BucketMinutes = 30, Buckets = 48, KeepDays = 3, Parallel = 6;

    public sealed record Sample(bool Ok, int? Ms, string? Error);
    public sealed record Down(string At, int Minutes);
    public sealed record Row(long Id, long HostId, string Name, string Device, string Ip, string Kind, int Port, string Path, bool Https, int SlowMs,
        string State, string Strip, double? Uptime, int? Ms, string? Error, string? DownSince, Down? LastDown, int Checks);
    public sealed record Suggestion(long HostId, string Device, string Ip, string Name, string Kind, int Port, bool Https, string Path);

    private sealed class Track { public int Fails; public bool Down, Told; public string FirstFail = ""; }

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly NetworkTools _tools;
    private readonly ILogger<ServiceWatch> _log;
    private readonly Dictionary<long, Track> _track = new();
    private readonly LatencyEpisodes _slow = new();
    private readonly object _trackLock = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private DateTime _pruned = DateTime.MinValue;

    /// <summary>One check of one service at an address. Swapped out by tests.</summary>
    internal Func<HostStore.WatchedService, string, CancellationToken, Task<Sample>> Probe { get; set; }

    public ServiceWatch(HostStore store, ScannerService scanner, NetworkTools tools, ILogger<ServiceWatch> log)
    {
        _store = store; _scanner = scanner; _tools = tools; _log = log;
        Probe = Real;
    }

    public bool Enabled => _store.GetSetting("serviceWatch") != "false";

    // ------------------------------------------------------------ the checks

    public static string Url(HostStore.WatchedService s, string ip) => $"{(s.Https ? "https" : "http")}://{ip}:{s.Port}{(s.Path.StartsWith('/') ? s.Path : "/" + s.Path)}";

    private async Task<Sample> Real(HostStore.WatchedService s, string ip, CancellationToken ct)
    {
        if (s.Kind == "port") return await PortOnce(ip, s.Port, ct);
        try
        {
            var data = await _tools.HttpCheck(Url(s, ip), ct);
            var last = data.Steps[^1];
            var ms = data.Steps.Sum(x => x.TotalMs);
            if (last.Error is not null) return new Sample(false, null, last.Error);
            return last.Status is >= 200 and < 400
                ? new Sample(true, ms, null)
                : new Sample(false, ms, $"It answered {last.Status} {last.Reason}".Trim() + ", not a normal answer.");
        }
        catch (ArgumentException ex) { return new Sample(false, null, ex.Message); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new Sample(false, null, ex.Message); }
    }

    public static async Task<Sample> PortOnce(string ip, int port, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(System.Net.IPAddress.Parse(ip), port, limit.Token);
            return new Sample(true, (int)sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new Sample(false, null, $"Nothing answered on port {port} within 3 seconds."); }
        catch (SocketException ex) { return new Sample(false, null, ex.SocketErrorCode == SocketError.ConnectionRefused ? $"Nothing is listening on port {port}." : $"Couldn't connect to port {port}: {ex.Message}"); }
        catch (FormatException) { return new Sample(false, null, "That isn't an address."); }
    }

    /// <summary>A name for a service on a port, from what that port usually is.</summary>
    public static string NameFor(int port, string kind)
    {
        if (kind == "web" && port is 80 or 443 or 8080 or 8443 or 5000 or 5001) return "Web page";
        var known = PortChecker.CommonPorts.FirstOrDefault(p => p.Port == port)?.Service;
        return known is { Length: > 0 } ? known : kind == "web" ? "Web page" : $"Port {port}";
    }

    // ------------------------------------------------------------ running them

    /// <summary>Checks every watched service once and says what needs saying. Null if a round was already running.</summary>
    public async Task<int?> RunOnce(CancellationToken ct, DateTime? nowUtc = null)
    {
        if (!await _busy.WaitAsync(0, ct)) return null;
        try
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var hosts = _store.GetAll().ToDictionary(h => h.Id);
            var services = _store.GetServices();
            lock (_trackLock)
                foreach (var gone in _track.Keys.Where(k => services.All(s => s.Id != k)).ToList()) { _track.Remove(gone); _slow.Forget(gone); }
            var gate = new SemaphoreSlim(Parallel);
            var n = 0;
            await Task.WhenAll(services.Select(async s =>
            {
                if (!hosts.TryGetValue(s.HostId, out var h) || h.Forgotten || h.Ignored || !System.Net.IPAddress.TryParse(h.Ip, out _)) return;
                await gate.WaitAsync(ct);
                try
                {
                    var off = !h.Online;
                    var sample = await Probe(s, h.Ip, ct);
                    _store.AddServiceCheck(s.Id, now, sample.Ok, sample.Ms, sample.Error, off && !sample.Ok);
                    await Alerts(s, h, sample, off, now, ct);
                    Interlocked.Increment(ref n);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "Checking {Service} failed", s.Name); }
                finally { gate.Release(); }
            }));
            if (now - _pruned > TimeSpan.FromHours(1)) { _store.PruneServiceChecks(now.AddDays(-KeepDays)); _pruned = now; }
            return n;
        }
        finally { _busy.Release(); }
    }

    private async Task Alerts(HostStore.WatchedService s, HostRecord h, Sample sample, bool deviceOff, DateTime now, CancellationToken ct)
    {
        var device = h.CustomName != "" ? h.CustomName : h.Hostname is { Length: > 0 } and not "—" ? h.Hostname : h.Ip;
        var what = $"{s.Name} on {device}";
        Track t;
        lock (_trackLock) { if (!_track.TryGetValue(s.Id, out t!)) _track[s.Id] = t = new Track(); }
        var quiet = _store.IsSnoozed(h.Id);
        if (!sample.Ok)
        {
            _slow.Observe(s.Id, null, s.SlowMs);
            if (deviceOff) { t.Fails = 0; return; }
            if (t.Fails == 0) t.FirstFail = now.ToString("o");
            t.Fails++;
            if (t.Down || t.Fails < FailsToAlert) return;
            t.Down = true;
            if (quiet || !Enabled) return;
            t.Told = true;
            await _scanner.SendGenericAlert($"{what} isn't answering",
                $"{s.Name} ({(s.Kind == "web" ? Url(s, h.Ip) : $"{h.Ip} port {s.Port}")}) has failed its last {FailsToAlert} checks: {sample.Error ?? "no answer"} " +
                (h.Online ? $"{device} itself is still answering, so it's the service, not the device. BAMF will say when it's back." : "BAMF will say when it's back."), "status", ct);
            return;
        }
        t.Fails = 0;
        if (t.Down)
        {
            t.Down = false;
            if (t.Told && !quiet)
            {
                var mins = DateTime.TryParse(t.FirstFail, null, System.Globalization.DateTimeStyles.RoundtripKind, out var f) ? Math.Max(1, (int)Math.Round((now - f.ToUniversalTime()).TotalMinutes)) : 0;
                await _scanner.SendGenericAlert($"{what} is answering again",
                    $"{s.Name} on {device} ({h.Ip}) is back" + (mins > 0 ? $" after about {mins} minute{(mins == 1 ? "" : "s")}." : "."), "status-up", ct);
            }
            t.Told = false;
        }
        if (s.SlowMs <= 0 || sample.Ms is not { } ms) return;
        var ep = _slow.Observe(s.Id, ms, s.SlowMs);
        if (ep is null || quiet || !Enabled) return;
        if (ep == "high")
            await _scanner.SendGenericAlert($"{what} is slow to answer: {FormatMs(ms)}",
                $"{s.Name} on {device} has taken {FormatMs(s.SlowMs)} or more to answer its last {LatencyEpisodes.HighProbes} checks, {FormatMs(ms)} this time. It's up, but anything using it will feel it. BAMF will say when it's back under {FormatMs(s.SlowMs)}.", "status", ct);
        else
            await _scanner.SendGenericAlert($"{what} is answering normally again: {FormatMs(ms)}",
                $"{s.Name} on {device} has answered its last {LatencyEpisodes.BackProbes} checks in under {FormatMs(s.SlowMs)}, after a slow spell that reached {FormatMs(_slow.Worst(s.Id))}.", "status-up", ct);
    }

    public static string FormatMs(int ms) => ms >= 1000 ? $"{ms / 1000.0:0.#} s" : $"{ms} ms";

    // ------------------------------------------------------------ what the card shows

    public IReadOnlyList<Row> Snapshot(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var hosts = _store.GetAll().ToDictionary(h => h.Id);
        var rows = new List<Row>();
        foreach (var s in _store.GetServices())
        {
            if (!hosts.TryGetValue(s.HostId, out var h) || h.Forgotten) continue;
            var checks = _store.GetServiceChecks(s.Id, now.AddHours(-24));
            var device = h.CustomName != "" ? h.CustomName : h.Hostname is { Length: > 0 } and not "—" ? h.Hostname : h.Ip;
            rows.Add(Summarize(s, device, h.Ip, !h.Online, checks, now, _slow.IsHigh(s.Id)));
        }
        return rows;
    }

    /// <summary>The row for one service from its last 24 hours of readings. Pure, so it can be reasoned about.</summary>
    public static Row Summarize(HostStore.WatchedService s, string device, string ip, bool deviceOff, IReadOnlyList<HostStore.ServiceCheck> checks, DateTime nowUtc, bool slowNow)
    {
        var start = nowUtc.AddHours(-24);
        var cells = new char[Buckets];
        var perBucket = Enumerable.Range(0, Buckets).Select(_ => new List<HostStore.ServiceCheck>()).ToArray();
        foreach (var c in checks)
        {
            if (!DateTime.TryParse(c.At, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)) continue;
            var i = (int)((at.ToUniversalTime() - start).TotalMinutes / BucketMinutes);
            if (i >= 0 && i < Buckets) perBucket[i].Add(c);
        }
        for (var i = 0; i < Buckets; i++)
        {
            var b = perBucket[i];
            if (b.Count == 0) { cells[i] = 'n'; continue; }
            var fails = b.Count(c => !c.Ok && !c.DeviceOff);
            cells[i] = fails >= 2 ? 'd'
                : s.SlowMs > 0 && b.Count(c => c.Ok && c.Ms >= s.SlowMs) >= 3 ? 's'
                : b.All(c => c.DeviceOff) ? 'o' : 'u';
        }
        var counted = checks.Where(c => !c.DeviceOff).ToList();
        double? uptime = counted.Count == 0 ? null : Math.Round(100.0 * counted.Count(c => c.Ok) / counted.Count, 1);
        var last = checks.Count > 0 ? checks[^1] : null;

        // The run of failures ending now, if any; and the latest earlier run of two or more.
        var runs = new List<(string From, string To, int Count)>();
        string? from = null, to = null; var n = 0;
        foreach (var c in checks)
        {
            if (!c.Ok && !c.DeviceOff) { from ??= c.At; to = c.At; n++; }
            else if (from is not null) { runs.Add((from, to!, n)); from = null; n = 0; }
        }
        var open = from is not null && n >= FailsToAlert ? (From: from, To: to!, Count: n) : ((string From, string To, int Count)?)null;
        if (from is not null) runs.Add((from, to!, n));
        string? downSince = open is { } o && !deviceOff ? o.From : null;
        Down? lastDown = null;
        foreach (var r in runs.Where(r => r.Count >= FailsToAlert && (open is null || r.From != open.Value.From)).OrderBy(r => r.From).TakeLast(1))
        {
            var a = DateTime.Parse(r.From, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
            var b = DateTime.Parse(r.To, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
            lastDown = new Down(r.From, Math.Max(1, (int)Math.Round((b - a).TotalMinutes) + 1));
        }
        var state = deviceOff ? "off" : last is null ? "waiting" : downSince is not null ? "down" : slowNow && last.Ok ? "slow" : "up";
        return new Row(s.Id, s.HostId, s.Name, device, ip, s.Kind, s.Port, s.Path, s.Https, s.SlowMs, state, new string(cells), uptime,
            last is { Ok: true } ? last.Ms : null, last is { Ok: false } ? last.Error : null, downSince, lastDown, checks.Count);
    }

    // ------------------------------------------------------------ suggestions

    private static readonly Dictionary<int, bool> WebPorts = new()
    { [80] = false, [443] = true, [8080] = false, [8443] = true, [8123] = false, [8096] = false, [32400] = false, [5000] = false, [5001] = true, [1880] = false };
    private static readonly HashSet<int> PlainPorts = new() { 22, 445, 554, 1883, 53, 3306, 5432, 6379, 2049, 548, 631, 9100, 8883 };

    /// <summary>Open ports BAMF has found on online known devices that aren't being watched yet: a web page or a port each, at most a dozen.</summary>
    public IReadOnlyList<Suggestion> Suggestions()
    {
        var existing = _store.GetServices();
        var open = _store.OpenPorts();
        var list = new List<Suggestion>();
        foreach (var h in _store.GetAll().Where(h => h.Online && !h.Forgotten && !h.Ignored && System.Net.IPAddress.TryParse(h.Ip, out _)).OrderBy(h => h.CustomName != "" ? h.CustomName : h.Hostname))
        {
            if (!open.TryGetValue(h.Id, out var ports)) continue;
            var device = h.CustomName != "" ? h.CustomName : h.Hostname is { Length: > 0 } and not "—" ? h.Hostname : h.Ip;
            foreach (var p in ports)
            {
                var web = WebPorts.TryGetValue(p, out var https);
                if (!web && !PlainPorts.Contains(p)) continue;
                var kind = web ? "web" : "port";
                if (existing.Any(e => e.HostId == h.Id && e.Port == p && e.Kind == kind)) continue;
                list.Add(new Suggestion(h.Id, device, h.Ip, NameFor(p, kind), kind, p, web && https, "/"));
            }
        }
        return list.Take(12).ToList();
    }

    // ------------------------------------------------------------ the loop

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { if (Enabled) await RunOnce(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "The service watch failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
