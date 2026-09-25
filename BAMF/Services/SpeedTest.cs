using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace LanWatch.Services;

/// <summary>
/// How fast is the line out of the house? BAMF times a download and an upload
/// against Cloudflare's speed test (speed.cloudflare.com), from the machine
/// BAMF runs on, so it measures the connection rather than whichever Wi-Fi
/// the dashboard is open over. Plain HTTPS: nothing to install and no key.
///
/// Off unless switched on, and then once a day or every six hours, or when
/// someone presses Run now. Each test is capped in bytes as well as time,
/// about 100 MB down and 25 MB up, so a fast line finishes early instead of
/// pouring gigabytes through, and a slow one stops at the time limit.
///
/// The line is full while it runs, so the internet watch sits the test out
/// rather than calling it slow.
/// </summary>
public sealed class SpeedTest : BackgroundService
{
    public const long DownBudget = 100_000_000, UpBudget = 25_000_000;
    private const int Streams = 4;
    private static readonly TimeSpan PhaseLimit = TimeSpan.FromSeconds(15);
    private const string Server = "https://speed.cloudflare.com";

    /// <summary>What a test in progress is doing, for the card to show as it goes.</summary>
    public sealed record Progress(string Phase, double? Mbps);

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly IConfiguration _cfg;
    private readonly ILogger<SpeedTest> _log;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly string _version;
    private DateTime _quietUntil = DateTime.MinValue;
    private CancellationToken _stopping;

    public SpeedTest(HostStore store, ScannerService scanner, IConfiguration cfg, ILogger<SpeedTest> log)
    {
        _store = store; _scanner = scanner; _cfg = cfg; _log = log;
        _version = typeof(SpeedTest).Assembly.GetName().Version?.ToString(3) ?? "1";
    }

    /// <summary>"off", "daily" (at ten past four in the morning) or "6h" (ten past midnight, six, noon and six).</summary>
    public string Schedule
    {
        get
        {
            var v = (_store.GetSetting("speedTest") ?? _cfg["Bamf:SpeedTest"] ?? "off").Trim().ToLowerInvariant();
            return v is "daily" or "6h" ? v : "off";
        }
    }

    public bool Running { get; private set; }
    public Progress? Now { get; private set; }

    /// <summary>True while a test is filling the line, and for a few seconds after.</summary>
    public bool LineBusy => Running || DateTime.UtcNow < _quietUntil;

    /// <summary>The usual download and upload: the median of the last ten tests that worked.</summary>
    public (double Down, double Up)? Usual(IEnumerable<HostStore.SpeedResult>? before = null)
    {
        var ok = (before ?? _store.GetSpeedResults(90)).Where(r => r.Error is null).TakeLast(10).ToList();
        if (ok.Count < 3) return null;
        static double Median(IEnumerable<double> xs) { var s = xs.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
        return (Median(ok.Select(r => r.DownMbps)), Median(ok.Select(r => r.UpMbps)));
    }

    /// <summary>Starts a test now, unless one is already running. It carries on in the background.</summary>
    public bool RunNow()
    {
        if (!_one.Wait(0)) return false;
        _ = Task.Run(async () =>
        {
            try { await RunAsync(manual: true, _stopping); }
            finally { _one.Release(); }
        });
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _stopping = ct;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { break; }
            try
            {
                if (!Due()) continue;
                if (!await _one.WaitAsync(0, ct)) continue;
                try { await RunAsync(manual: false, ct); }
                finally { _one.Release(); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogDebug(ex, "Speed test schedule tick failed"); }
        }
    }

    /// <summary>
    /// Whether the scheduled slot we're in hasn't had its test. A slot that
    /// was missed by more than an hour (BAMF was off, say) is skipped rather
    /// than run late, since a test at four in the morning is one thing and the
    /// same test in the middle of the afternoon is another.
    /// </summary>
    private bool Due()
    {
        var schedule = Schedule;
        if (schedule == "off") return false;
        // The latest slot that has started: ten past four, or ten past every sixth hour.
        var now = DateTime.Now;
        var slot = schedule == "daily" ? now.Date.AddHours(4).AddMinutes(10) : now.Date.AddMinutes(10);
        var step = schedule == "daily" ? TimeSpan.FromDays(1) : TimeSpan.FromHours(6);
        while (slot > now) slot -= step;
        while (slot + step <= now) slot += step;
        if (now - slot > TimeSpan.FromHours(1)) return false;
        var last = _store.GetSpeedResults(2).LastOrDefault(r => !r.Manual);
        return last is null || DateTime.Parse(last.At, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime() < slot;
    }

    private async Task RunAsync(bool manual, CancellationToken ct)
    {
        Running = true;
        Now = new Progress("ping", null);
        var at = DateTime.UtcNow;
        long used = 0;
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler
            {
                MaxConnectionsPerServer = Streams * 2,
                AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            }) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BAMF", _version));

            var (ping, jitter, where) = await Latency(http, ct);
            Now = new Progress("download", null);
            var (down, gotDown) = await Download(http, ct);
            used += gotDown;
            Now = new Progress("upload", null);
            var (up, sentUp) = await Upload(http, ct);
            used += sentUp;

            var before = _store.GetSpeedResults(90);
            var result = new HostStore.SpeedResult(at.ToString("o"), Math.Round(down, 1), Math.Round(up, 1), ping, jitter,
                where, (int)Math.Round(used / 1e6), manual, null);
            _store.AddSpeedResult(result);
            _log.LogInformation("Speed test: {Down} Mbps down, {Up} Mbps up, {Ping} ms via Cloudflare {Where}, {Mb} MB",
                result.DownMbps, result.UpMbps, ping, where, result.MegabytesUsed);
            if (!manual) await CheckSlower(result, before, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var why = ex is HttpRequestException h && h.StatusCode is { } code ? $"Cloudflare answered HTTP {(int)code}"
                : ex.GetBaseException().Message;
            _log.LogWarning("Speed test failed: {Why}", why);
            _store.AddSpeedResult(new HostStore.SpeedResult(at.ToString("o"), 0, 0, 0, 0, "", (int)Math.Round(used / 1e6), manual, why));
        }
        finally
        {
            Running = false;
            Now = null;
            _quietUntil = DateTime.UtcNow.AddSeconds(10);
        }
    }

    /// <summary>
    /// The round trip to the test server, from ten small requests on a warm
    /// connection. Cloudflare says in each answer how long the TCP round trip
    /// is by its own reckoning (the cfL4 server timing), which is the line and
    /// nothing else, and that's used when it's there. Otherwise it's the time
    /// to the answer less the time Cloudflare says it spent making it. The
    /// first request is dropped, since it carries the handshake. Of the rest
    /// of what comes back, only which Cloudflare site answered is kept.
    /// </summary>
    private async Task<(int Ping, int Jitter, string Where)> Latency(HttpClient http, CancellationToken ct)
    {
        var rtts = new List<double>();
        var vars = new List<double>();
        var timed = new List<double>();
        var where = "";
        for (var i = 0; i < 10; i++)
        {
            var sw = Stopwatch.StartNew();
            using var resp = await http.GetAsync($"{Server}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, ct);
            var ms = sw.Elapsed.TotalMilliseconds;
            resp.EnsureSuccessStatusCode();
            if (where == "" && resp.Headers.TryGetValues("colo", out var colo)) where = colo.FirstOrDefault()?.Trim() ?? "";
            if (i == 0) continue;
            var timing = resp.Headers.TryGetValues("server-timing", out var t) ? string.Join(",", t) : "";
            var rtt = Regex.Match(timing, @"[?&]rtt=(\d+)");
            var rttVar = Regex.Match(timing, @"[?&]rtt_var=(\d+)");
            if (rtt.Success)
            {
                rtts.Add(double.Parse(rtt.Groups[1].Value) / 1000);   // microseconds
                if (rttVar.Success) vars.Add(double.Parse(rttVar.Groups[1].Value) / 1000);
            }
            var spent = Regex.Matches(timing, @"dur=([\d.]+)").Sum(m =>
                double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0);
            timed.Add(Math.Max(0, ms - spent));
        }
        static double Median(List<double> xs) { var o = xs.OrderBy(x => x).ToList(); return o[o.Count / 2]; }
        if (rtts.Count >= 5)
            return ((int)Math.Round(Median(rtts)), vars.Count > 0 ? (int)Math.Round(Median(vars)) : 0, where);
        var jitter = timed.Zip(timed.Skip(1), (a, b) => Math.Abs(a - b)).DefaultIfEmpty(0).Average();
        return ((int)Math.Round(Median(timed)), (int)Math.Round(jitter), where);
    }

    private Task<(double Mbps, long Bytes)> Download(HttpClient http, CancellationToken ct) =>
        Measure(DownBudget, ct, async (count, stop) =>
        {
            using var resp = await http.GetAsync($"{Server}/__down?bytes={DownBudget / Streams}", HttpCompletionOption.ResponseHeadersRead, stop);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(stop);
            var buf = new byte[81920];
            int n;
            while ((n = await s.ReadAsync(buf, stop)) > 0) count(n);
        });

    private Task<(double Mbps, long Bytes)> Upload(HttpClient http, CancellationToken ct) =>
        Measure(UpBudget, ct, async (count, stop) =>
        {
            using var body = new CountingContent(UpBudget / Streams, count);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var resp = await http.PostAsync($"{Server}/__up", body, stop);
            resp.EnsureSuccessStatusCode();
        });

    /// <summary>
    /// Runs four transfers at once and times them. The rate is taken from once
    /// the connections have got going (a second in, or 15% of the bytes, which
    /// on a fast line comes first) to when the first of them finishes, so
    /// neither the slow start nor the stragglers at the end drag it down. The
    /// whole phase stops at the time limit whatever's left.
    /// </summary>
    private async Task<(double Mbps, long Bytes)> Measure(long budget, CancellationToken ct, Func<Action<int>, CancellationToken, Task> transfer)
    {
        long total = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(PhaseLimit);
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, Streams).Select(_ => transfer(n => Interlocked.Add(ref total, n), stop.Token)).ToList();
        var all = Task.WhenAll(tasks);
        (double T, long B)? warm = null, firstDone = null;
        var last = (T: 0.0, B: 0L);
        while (!all.IsCompleted)
        {
            await Task.WhenAny(all, Task.Delay(100, CancellationToken.None));
            var now = (T: sw.Elapsed.TotalSeconds, B: Interlocked.Read(ref total));
            if (warm is null && (now.T >= 1 || now.B >= budget * .15)) warm = now;
            if (firstDone is null && tasks.Any(t => t.IsCompleted)) firstDone = now;
            if (now.T - last.T >= .5 || all.IsCompleted)
            {
                Now = Now! with { Mbps = Math.Round((now.B - last.B) * 8 / (now.T - last.T) / 1e6, 1) };
                last = now;
            }
        }
        try { await all; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* the time limit: measure what arrived */ }
        var end = (T: sw.Elapsed.TotalSeconds, B: Interlocked.Read(ref total));
        if (end.B < budget / 50) throw new HttpRequestException("Too little got through to measure.");
        var from = warm ?? (0, 0);
        var to = firstDone is { } f && f.T - from.T >= .5 ? f : end;
        var mbps = to.T - from.T >= .3 ? (to.B - from.B) * 8 / (to.T - from.T) / 1e6 : end.B * 8 / end.T / 1e6;
        return (mbps, end.B);
    }

    /// <summary>
    /// A scheduled test that comes in at under half the usual, down or up, is
    /// worth saying something about. The usual is the tests before this one,
    /// so one bad morning doesn't lower the bar for itself.
    /// </summary>
    private async Task CheckSlower(HostStore.SpeedResult r, List<HostStore.SpeedResult> before, CancellationToken ct)
    {
        if (Usual(before) is not { } usual) return;
        var slowDown = r.DownMbps < usual.Down * .5;
        var slowUp = r.UpMbps < usual.Up * .5;
        if (!slowDown && !slowUp) return;
        var what = slowDown && slowUp ? "Download and upload are" : slowDown ? "Download is" : "Upload is";
        await _scanner.RaiseSecurity("The internet is slower than usual",
            $"{what} well under the usual. Download {Mbps(r.DownMbps)} against a usual {Mbps(usual.Down)}; " +
            $"upload {Mbps(r.UpMbps)} against {Mbps(usual.Up)}; {r.PingMs} ms to Cloudflare {r.Server}. " +
            "The usual is the median of the last ten tests. One slow test can be something else busy on the line; " +
            "if the next one's slow too, it's worth asking your provider.", ct, "internet");
    }

    public static string Mbps(double v) => v >= 100 ? $"{v:0} Mbps" : $"{v:0.#} Mbps";

    /// <summary>An upload body of a set size that says how much of it has gone.</summary>
    private sealed class CountingContent : HttpContent
    {
        private readonly long _size;
        private readonly Action<int> _sent;
        public CountingContent(long size, Action<int> sent) { _size = size; _sent = sent; }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            var buf = new byte[65536];
            Random.Shared.NextBytes(buf);
            for (var left = _size; left > 0;)
            {
                var n = (int)Math.Min(buf.Length, left);
                await stream.WriteAsync(buf.AsMemory(0, n), ct);
                _sent(n);
                left -= n;
            }
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override bool TryComputeLength(out long length) { length = _size; return true; }
    }
}
