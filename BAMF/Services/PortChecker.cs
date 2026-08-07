using System.Net.Sockets;

namespace LanWatch.Services;

/// <summary>
/// On-demand port checker. Probes either a built-in set of common service
/// ports or a caller-supplied list, for one host. Never runs automatically.
/// </summary>
public static class PortChecker
{
    public record PortInfo(int Port, string Service);

    // Common, identity-revealing ports (the default set).
    private static readonly PortInfo[] Common =
    {
        new(80,   "HTTP"),
        new(443,  "HTTPS"),
        new(22,   "SSH"),
        new(21,   "FTP"),
        new(23,   "Telnet"),
        new(445,  "SMB"),
        new(139,  "NetBIOS"),
        new(3389, "RDP"),
        new(5900, "VNC"),
        new(8080, "HTTP-alt"),
        new(8443, "HTTPS-alt"),
        new(53,   "DNS"),
        new(548,  "AFP"),
        new(631,  "IPP/Print"),
        new(9100, "RAW/Print"),
        new(32400,"Plex"),
        new(1883, "MQTT"),
        new(5000, "UPnP/Web"),
    };

    private static readonly Dictionary<int, string> ServiceNames =
        Common.ToDictionary(p => p.Port, p => p.Service);

    public static string ServiceName(int port) =>
        ServiceNames.TryGetValue(port, out var s) ? s : "";

    /// <summary>Scan the default common-port set.</summary>
    public static Task<List<PortInfo>> ScanAsync(string ip, int timeoutMs, CancellationToken ct) =>
        ScanAsync(ip, Common.Select(p => p.Port), timeoutMs, ct);

    /// <summary>Scan a specific set of ports.</summary>
    public static async Task<List<PortInfo>> ScanAsync(string ip, IEnumerable<int> ports, int timeoutMs, CancellationToken ct)
    {
        var open = new List<PortInfo>();
        // Bound concurrency so a wide range doesn't open thousands of sockets at once.
        using var sem = new SemaphoreSlim(128);
        var tasks = ports.Distinct().Where(p => p is > 0 and <= 65535).Select(async port =>
        {
            await sem.WaitAsync(ct);
            try
            {
                if (await IsOpen(ip, port, timeoutMs, ct))
                    lock (open) open.Add(new PortInfo(port, ServiceName(port)));
            }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);
        return open.OrderBy(p => p.Port).ToList();
    }

    private static async Task<bool> IsOpen(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(ip, port, ct).AsTask();
            var done = await Task.WhenAny(connect, Task.Delay(timeoutMs, ct));
            return done == connect && client.Connected;
        }
        catch { return false; }
    }

    /// <summary>
    /// Parses a port spec like "22,80,443,8000-8100" into a de-duplicated list.
    /// Returns null if nothing valid is found.
    /// </summary>
    public static List<int>? ParseSpec(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var ports = new HashSet<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-'))
            {
                var ends = part.Split('-', 2);
                if (int.TryParse(ends[0], out var a) && int.TryParse(ends[1], out var b))
                {
                    if (a > b) (a, b) = (b, a);
                    // cap a single range so nobody accidentally scans all 65535 x many hosts
                    b = Math.Min(b, a + 2000);
                    for (var p = Math.Max(1, a); p <= Math.Min(65535, b); p++) ports.Add(p);
                }
            }
            else if (int.TryParse(part, out var p) && p is > 0 and <= 65535)
            {
                ports.Add(p);
            }
        }
        return ports.Count > 0 ? ports.OrderBy(p => p).ToList() : null;
    }
}
