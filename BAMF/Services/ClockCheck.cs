using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// Is this machine's clock right? Every six hours, one SNTP question to a time server (the one you name, else one the network's DHCP offers,
/// else pool.ntp.org), and an alert when the clock is out by more than you allow (30 seconds unless you change it). A wrong clock quietly
/// breaks things BAMF depends on: the times in every alert and timeline, certificates that look expired or not yet valid, the hours of the
/// quiet hours and off-hours rules. Off by default, because the question leaves the house (one 48-byte UDP packet, sent to port 123).
/// </summary>
public sealed class ClockCheck : BackgroundService
{
    public sealed record Result(string At, string Server, double? OffsetSeconds, int? RoundTripMs, string? Error);

    public const int DefaultMaxSeconds = 30;
    public const string DefaultServer = "pool.ntp.org";
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly DhcpProbe _dhcp;
    private readonly ILogger<ClockCheck> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);

    /// <summary>Sends the request and returns the reply with the moment it arrived. Swapped out by tests; the real one uses a UDP socket.</summary>
    internal Func<string, byte[], CancellationToken, Task<(byte[] Reply, DateTime ArrivedUtc)>> Exchange { get; set; } = RealExchange;

    public ClockCheck(HostStore store, ScannerService scanner, DhcpProbe dhcp, ILogger<ClockCheck> log)
    {
        _store = store; _scanner = scanner; _dhcp = dhcp; _log = log;
    }

    public bool Enabled => _store.GetSetting("clockCheck") == "true";

    public int MaxSeconds => int.TryParse(_store.GetSetting("clockMaxSeconds"), out var s) ? Math.Clamp(s, 1, 3600) : DefaultMaxSeconds;

    /// <summary>The server that was typed in, if any.</summary>
    public string ServerSetting => (_store.GetSetting("clockServer") ?? "").Trim();

    /// <summary>Who is asked: what was typed, else a time server the network hands out, else the public pool.</summary>
    public string Server
    {
        get
        {
            if (ServerSetting != "") return ServerSetting;
            var offered = _dhcp.Last?.Networks.SelectMany(n => n.Offers).SelectMany(o => o.Ntp).FirstOrDefault(a => a != "" && a != "0.0.0.0");
            return offered ?? DefaultServer;
        }
    }

    public Result? Last
    {
        get
        {
            try { return JsonSerializer.Deserialize<Result>(_store.GetSetting("clockResult") ?? "null"); }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>Whether what was typed can be a server's name or address: letters, digits, dots, dashes and colons, and not absurdly long.</summary>
    public static bool ValidServer(string s) =>
        s.Length is >= 1 and <= 253 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':');

    // ------------------------------------------------------------ the packets (SNTP, RFC 4330)

    private static readonly DateTime NtpEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The 48-byte client request, carrying the moment it was sent so the reply can be matched to it.</summary>
    internal static byte[] Request(DateTime sentUtc)
    {
        var p = new byte[48];
        p[0] = 0x23;                                                   // no warning, version 4, client
        WriteTimestamp(p, 40, sentUtc);
        return p;
    }

    private static void WriteTimestamp(byte[] p, int at, DateTime utc)
    {
        var span = utc - NtpEpoch;
        var secs = (ulong)span.TotalSeconds;
        var frac = (uint)((span.Ticks % TimeSpan.TicksPerSecond) * 4294967296.0 / TimeSpan.TicksPerSecond);
        for (var i = 0; i < 4; i++) { p[at + i] = (byte)(secs >> (24 - 8 * i)); p[at + 4 + i] = (byte)(frac >> (24 - 8 * i)); }
    }

    private static DateTime ReadTimestamp(byte[] p, int at)
    {
        ulong secs = 0, frac = 0;
        for (var i = 0; i < 4; i++) { secs = secs << 8 | p[at + i]; frac = frac << 8 | p[at + 4 + i]; }
        if ((secs & 0x80000000UL) == 0) secs += 1UL << 32;             // the era after 2036
        return NtpEpoch.AddSeconds(secs).AddTicks((long)(frac * (double)TimeSpan.TicksPerSecond / 4294967296.0));
    }

    /// <summary>
    /// How far this machine's clock is from the server's (positive: this clock is behind) and the round trip, from a reply to <paramref name="sent"/>.
    /// Null for a reply that isn't an answer to it, from a server that says it isn't synchronised, or a "kiss of death".
    /// </summary>
    internal static (double OffsetSeconds, double RoundTripSeconds)? Read(byte[] reply, DateTime sent, DateTime arrived)
    {
        if (reply.Length < 48 || (reply[0] & 7) != 4 || reply[1] == 0 || (reply[0] >> 6) == 3) return null;
        // The reply repeats our transmit time: one that doesn't wasn't an answer to us.
        var echoed = ReadTimestamp(reply, 24);
        if (Math.Abs((echoed - sent).TotalMilliseconds) > 1) return null;
        var t1 = ReadTimestamp(reply, 32);                             // when the server received it
        var t2 = ReadTimestamp(reply, 40);                             // when the server answered
        var offset = ((t1 - sent) + (t2 - arrived)).TotalSeconds / 2;
        var trip = ((arrived - sent) - (t2 - t1)).TotalSeconds;
        return (offset, Math.Max(0, trip));
    }

    private static async Task<(byte[], DateTime)> RealExchange(string server, byte[] request, CancellationToken ct)
    {
        var addr = IPAddress.TryParse(server, out var ip) ? ip
            : (await Dns.GetHostAddressesAsync(server, AddressFamily.InterNetwork, ct)).FirstOrDefault() ?? throw new SocketException((int)SocketError.HostNotFound);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        await udp.SendAsync(request, new IPEndPoint(addr, 123), ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var got = await udp.ReceiveAsync(timeout.Token);
        return (got.Buffer, DateTime.UtcNow);
    }

    // ------------------------------------------------------------ the check

    /// <summary>Asks once, writes the answer down, and says if the clock is out. Null if a check was already running. <paramref name="nowUtc"/> is for tests.</summary>
    public async Task<Result?> Check(CancellationToken ct, DateTime? nowUtc = null)
    {
        if (!await _busy.WaitAsync(0, ct)) return null;
        try
        {
            var server = Server;
            Result result;
            try
            {
                var sent = nowUtc ?? DateTime.UtcNow;
                var (reply, arrived) = await Exchange(server, Request(sent), ct);
                var read = Read(reply, sent, arrived);
                result = read is { } r
                    ? new Result(sent.ToString("o"), server, Math.Round(r.OffsetSeconds, 3), (int)Math.Round(r.RoundTripSeconds * 1000), null)
                    : new Result(sent.ToString("o"), server, null, null, "It answered, but not with a time it could use.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or TimeoutException)
            {
                result = new Result((nowUtc ?? DateTime.UtcNow).ToString("o"), server, null, null,
                    ex is OperationCanceledException ? $"{server} didn't answer." : $"Couldn't ask {server}: {ex.Message}");
            }
            _store.SetSetting("clockResult", JsonSerializer.Serialize(result));
            await Say(result, nowUtc ?? DateTime.UtcNow, ct);
            return result;
        }
        finally { _busy.Release(); }
    }

    private async Task Say(Result r, DateTime now, CancellationToken ct)
    {
        if (r.OffsetSeconds is not { } off || Math.Abs(off) <= MaxSeconds) return;
        if (!Due("clockAlertAt", TimeSpan.FromHours(23), now)) return;
        var span = TimeSpan.FromSeconds(Math.Abs(off));
        var how = span.TotalMinutes >= 2 ? $"{(int)span.TotalMinutes} minutes" : $"{(int)span.TotalSeconds} seconds";
        var title = $"This machine's clock is {how} {(off > 0 ? "behind" : "ahead")}";
        var detail = $"{r.Server} says it is {now.AddSeconds(off).ToLocalTime():HH:mm:ss}; this machine says {now.ToLocalTime():HH:mm:ss}. You allow {MaxSeconds} seconds. " +
                     "A wrong clock puts the wrong times on every alert and timeline, makes HTTPS certificates look expired or not yet valid, and shifts quiet hours and off hours. " +
                     "Check that Windows' time service is running (Settings, Time and language, Sync now), or that the server's NTP is set.";
        _log.LogWarning("Clock: {Title}. {Detail}", title, detail);
        _store.AddAlert("clock", title, detail);
        await _scanner.SendGenericAlert(title, detail, "status", ct);
    }

    private bool Due(string key, TimeSpan every, DateTime now)
    {
        if (DateTime.TryParse(_store.GetSetting(key), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) && now - at.ToUniversalTime() < every) return false;
        _store.SetSetting(key, now.ToString("o"));
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var due = Last is { } l && DateTime.TryParse(l.At, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at.ToUniversalTime() + Every : DateTime.UtcNow;
                if (Enabled && DateTime.UtcNow >= due) await Check(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "The clock check failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(10), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
