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
    public DateTime? ActiveUntilUtc { get; private set; }

    /// <summary>Starts blinking, replacing any blink already running.</summary>
    public DateTime Start(long hostId, IPAddress ip, int seconds)
    {
        seconds = Math.Clamp(seconds, 5, MaxSeconds);
        var until = DateTime.UtcNow.AddSeconds(seconds);
        CancellationTokenSource cts;
        lock (_lock)
        {
            _cts?.Cancel();
            cts = _cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            ActiveHostId = hostId;
            ActiveUntilUtc = until;
        }
        _log.LogInformation("Blinking the switch port of {Ip} for {Seconds} s", ip, seconds);
        _ = Task.Run(() => RunAsync(ip, cts));
        return until;
    }

    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
            ActiveHostId = null;
            ActiveUntilUtc = null;
        }
    }

    private async Task RunAsync(IPAddress ip, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var payload = new byte[PayloadBytes];
        var target = new IPEndPoint(ip, DiscardPort);
        try
        {
            using var udp = new UdpClient(ip.AddressFamily);
            while (!ct.IsCancellationRequested)
            {
                // One second on...
                var onUntil = DateTime.UtcNow.AddSeconds(1);
                while (DateTime.UtcNow < onUntil && !ct.IsCancellationRequested)
                {
                    for (var i = 0; i < PacketsPerTick; i++)
                    {
                        try { await udp.SendAsync(payload, payload.Length, target); }
                        catch (SocketException) { /* host unreachable for a moment; keep the rhythm */ }
                    }
                    await Task.Delay(10, ct);
                }
                // ...one second off.
                await Task.Delay(1000, ct);
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
                    ActiveUntilUtc = null;
                }
            }
            cts.Dispose();
        }
    }
}
