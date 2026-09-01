using System.Net.NetworkInformation;
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

    /// <summary>Connect slots a single-host scan may use.</summary>
    public const int HostBudget = 128;

    /// <summary>
    /// Connect slots allowed against a default gateway. A router's management
    /// interface is one small embedded CPU, not a server, and hitting it with a
    /// wide burst of half-open connections is a reliable way to knock consumer
    /// gateway hardware offline. Only gateways pay this: every other host keeps
    /// the full budget. A default 18-port scan of a gateway takes three rounds
    /// instead of one; a wide range is where it really slows down, which is
    /// exactly the case worth slowing down.
    /// </summary>
    public const int GatewayBudget = 8;

    /// <summary>
    /// Per-host budget for a scan that fans out across several hosts at once,
    /// so the product of the two never exceeds what one host is allowed. Without
    /// this, 8 concurrent hosts x 128 ports each puts 1024 half-open sockets on
    /// the wire, which is enough to exhaust a consumer gateway's session table.
    /// </summary>
    public static int PerHostBudget(int hostConcurrency) =>
        Math.Max(4, HostBudget / Math.Max(1, hostConcurrency));

    private static HashSet<string>? _gateways;
    private static DateTime _gatewaysReadAt;
    private static readonly object _gatewayLock = new();

    /// <summary>
    /// Default-gateway addresses of every interface that is up. Cached briefly:
    /// a wildcard scan asks once per target, and the answer rarely changes.
    /// </summary>
    private static HashSet<string> Gateways()
    {
        lock (_gatewayLock)
        {
            if (_gateways is not null && DateTime.UtcNow - _gatewaysReadAt < TimeSpan.FromMinutes(5))
                return _gateways;

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var gw in nic.GetIPProperties().GatewayAddresses)
                        if (gw.Address is not null &&
                            gw.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !gw.Address.Equals(System.Net.IPAddress.Any))
                            found.Add(gw.Address.ToString());
                }
            }
            catch { /* best effort - failing to spot a gateway just means no clamp */ }

            _gateways = found;
            _gatewaysReadAt = DateTime.UtcNow;
            return found;
        }
    }

    /// <summary>True if the address is a default gateway on this machine.</summary>
    public static bool IsGateway(string ip) => Gateways().Contains(ip);

    /// <summary>Scan the default common-port set.</summary>
    public static Task<List<PortInfo>> ScanAsync(string ip, int timeoutMs, CancellationToken ct,
        int maxConcurrency = HostBudget) =>
        ScanAsync(ip, Common.Select(p => p.Port), timeoutMs, ct, maxConcurrency);

    /// <summary>Scan a specific set of ports.</summary>
    public static async Task<List<PortInfo>> ScanAsync(string ip, IEnumerable<int> ports, int timeoutMs,
        CancellationToken ct, int maxConcurrency = HostBudget)
    {
        var open = new List<PortInfo>();
        // Bound concurrency so a wide range doesn't open thousands of sockets at
        // once, and give a gateway a far smaller share than a regular host.
        var limit = Math.Max(1, maxConcurrency);
        if (IsGateway(ip)) limit = Math.Min(limit, GatewayBudget);
        using var sem = new SemaphoreSlim(limit);
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
