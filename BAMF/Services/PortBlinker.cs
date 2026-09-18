using System.Net;
using System.Net.Sockets;

namespace LanWatch.Services;

/// <summary>
/// Helps the user find which switch port a device is plugged into, on switches
/// that can't say (unmanaged, or budget "smart" switches with no visible MAC
/// table). For a short while it sends the device bursts of traffic, one second
/// on and one second off. A switch forwards a device's traffic only out of
/// that device's port, so that port's activity light pulses in a steady
/// rhythm the user can pick out against normal flicker.
///
/// The traffic is UDP to the discard port (9): every device receives it
/// whether or not it answers pings, it needs no raw-socket privilege on Linux
/// the way ICMP does, and a device has nothing to do with it but drop it.
/// On demand only, one device at a time, private addresses only.
/// </summary>
public class PortBlinker
{
    public const int DefaultSeconds = 30;
    public const int MaxSeconds = 60;

    private const int DiscardPort = 9;
    private const int PayloadBytes = 1000;
    private const int PacketsPerTick = 5;   // every ~10 ms while "on": a few hundred packets a second

    private readonly ILogger<PortBlinker> _log;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public PortBlinker(ILogger<PortBlinker> log) => _log = log;

    public long? ActiveHostId { get; private set; }
    public DateTime? ActiveStartedUtc { get; private set; }
    public DateTime? ActiveUntilUtc { get; private set; }

    /// <summary>
    /// Starts blinking, replacing any blink already running. Bursts run on a
    /// fixed schedule from the returned start time - on for [2k, 2k+1) seconds,
    /// off for [2k+1, 2k+2) - so the dashboard can pulse in step with the
    /// switch light from nothing more than the start time.
    /// </summary>
    public (DateTime Started, DateTime Until) Start(long hostId, IPAddress ip, int seconds)
    {
        seconds = Math.Clamp(seconds, 5, MaxSeconds);
        var started = DateTime.UtcNow;
        var until = started.AddSeconds(seconds);
        CancellationTokenSource cts;
        lock (_lock)
        {
            _cts?.Cancel();
            cts = _cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            ActiveHostId = hostId;
            ActiveStartedUtc = started;
            ActiveUntilUtc = until;
        }
        _log.LogInformation("Blinking the switch port of {Ip} for {Seconds} s", ip, seconds);
        _ = Task.Run(() => RunAsync(ip, started, cts));
        return (started, until);
    }

    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
            ActiveHostId = null;
            ActiveStartedUtc = null;
            ActiveUntilUtc = null;
        }
    }

    private async Task RunAsync(IPAddress ip, DateTime started, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var payload = new byte[PayloadBytes];
        var target = new IPEndPoint(ip, DiscardPort);
        try
        {
            using var udp = new UdpClient(ip.AddressFamily);
            // Each burst is timed from the start, not from the end of the last
            // one, so small delays never add up and the rhythm stays in step
            // with the dashboard for the whole run.
            for (var k = 0; !ct.IsCancellationRequested; k++)
            {
                var onFrom = started.AddSeconds(2 * k);
                var onUntil = onFrom.AddSeconds(1);
                var wait = onFrom - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                while (DateTime.UtcNow < onUntil && !ct.IsCancellationRequested)
                {
                    for (var i = 0; i < PacketsPerTick; i++)
                    {
                        try { await udp.SendAsync(payload, payload.Length, target); }
                        catch (SocketException) { /* host unreachable for a moment; keep the rhythm */ }
                    }
                    await Task.Delay(10, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "Port blink to {Ip} stopped early", ip); }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_cts, cts))
                {
                    _cts = null;
                    ActiveHostId = null;
                    ActiveStartedUtc = null;
                    ActiveUntilUtc = null;
                }
            }
            cts.Dispose();
        }
    }
}
