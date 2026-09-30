using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace LanWatch.Services;

/// <summary>Which networks BAMF scans: Settings, appsettings.json, or every network this machine is on.</summary>
public partial class ScannerService
{
    // ---------- which networks ----------
    // The networks saved in Settings, if any; otherwise Bamf:Subnets in
    // appsettings.json; otherwise every network this machine has an address on.
    // Settings can only add private networks no bigger than a sweep covers (see
    // CheckNetwork), so whoever can open the dashboard can't point BAMF, and the
    // port scans that follow its networks, anywhere else. The file can still
    // list anything.

    /// <summary>The networks saved in Settings, or null when there are none.</summary>
    public List<string>? SavedNetworks()
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(_store.GetSetting("subnets") ?? "");
            return list is { Count: > 0 } ? list : null;
        }
        catch (JsonException) { return null; }
    }

    private string[] FileNetworks()
    {
        // Blank entries are skipped: the Home Assistant add-on blanks the
        // file's list past its own, and all blank means auto-detect.
        var configured = (_config.GetSection("Bamf:Subnets").Get<string[]>() ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c)).ToArray();
        // Back-compat: single Bamf:Subnet string.
        var single = _config["Bamf:Subnet"];
        return configured.Length == 0 && !string.IsNullOrWhiteSpace(single) ? [single] : configured;
    }

    /// <summary>Where the networks come from: "settings", "file", or "auto" for every one this machine is on.</summary>
    public string NetworkSource => SavedNetworks() is not null ? "settings" : FileNetworks().Length > 0 ? "file" : "auto";

    /// <summary>The networks BAMF scans as of now, whether or not a scan has picked them up yet.</summary>
    public List<string> CurrentNetworks()
    {
        try { return ResolveSubnets().Select(s => $"{s.Network}/{s.Prefix}").ToList(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return []; }
    }

    /// <summary>Private address ranges, the only ones Settings can add networks in.</summary>
    private static readonly (IPAddress Network, int Prefix)[] PrivateRanges =
    [
        (IPAddress.Parse("10.0.0.0"), 8), (IPAddress.Parse("172.16.0.0"), 12), (IPAddress.Parse("192.168.0.0"), 16),
        (IPAddress.Parse("100.64.0.0"), 10), (IPAddress.Parse("169.254.0.0"), 16),
    ];

    /// <summary>The biggest network a sweep covers whole; see EnumerateSubnet.</summary>
    public const int WidestPrefix = 22;

    /// <summary>
    /// Whether a network can be added from Settings: IPv4, written as an
    /// address and a prefix, private, and no bigger than a sweep covers. Gives
    /// it back as its network address and prefix, 192.168.1.7/24 as
    /// 192.168.1.0/24, or says what's wrong with it.
    /// </summary>
    public static string? CheckNetwork(string? input, out string label)
    {
        label = "";
        var parts = (input ?? "").Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 32)
            return $"\"{input?.Trim()}\" isn't a network. Write it as an address and a prefix, like 192.168.1.0/24.";
        if (prefix < WidestPrefix)
            return $"A /{prefix} is bigger than BAMF sweeps: it covers 1,022 addresses at most, a /{WidestPrefix}. Use /{WidestPrefix} or smaller.";
        if (prefix > 30) return "That network has no room for devices. Use /30 or bigger.";
        var network = GetNetworkAddress(ip, prefix);
        if (!PrivateRanges.Any(r => prefix >= r.Prefix && InSubnet(network, r.Network, r.Prefix)))
            return "BAMF only adds private networks from here: 10.x, 172.16-31.x, 192.168.x, 100.64-127.x and 169.254.x. Others can still go in appsettings.json.";
        label = $"{network}/{prefix}";
        return null;
    }

    /// <summary>Every network this machine has an IPv4 address on, and the interface it's on.</summary>
    public static List<(IPAddress Network, int Prefix, string Interface)> DetectLocalNetworks()
    {
        var result = new List<(IPAddress, int, string)>();
        var seen = new HashSet<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var prefix = addr.PrefixLength;
                var network = GetNetworkAddress(addr.Address, prefix);
                if (seen.Add($"{network}/{prefix}"))
                    result.Add((network, prefix, nic.Name));
            }
        }
        return result;
    }

    private List<(IPAddress Network, int Prefix)> ResolveSubnets()
    {
        var result = new List<(IPAddress, int)>();

        // Preferred: the networks saved in Settings, then the list of CIDRs in Bamf:Subnets.
        var configured = SavedNetworks()?.ToArray() ?? FileNetworks();

        if (configured.Length > 0)
        {
            // De-duplicate. The same network listed twice - a typo, or a
            // command-line override replacing one index while the file still
            // supplies the other - used to reach the scheduler as two entries
            // with one label, and the per-network tables keyed by label threw
            // on the second, which failed every scan pass. Say so once.
            var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cidr in configured)
            {
                var parts = cidr.Trim().Split('/');
                var entry = (IPAddress.Parse(parts[0]), int.Parse(parts[1]));
                if (!seenLabels.Add($"{entry.Item1}/{entry.Item2}"))
                {
                    if (_warnedDuplicateSubnets.Add(cidr.Trim()))
                        _log.LogWarning("Bamf:Subnets lists {Subnet} more than once; using it once.", cidr.Trim());
                    continue;
                }
                result.Add(entry);
            }
            return result;
        }

        // Auto-detect: every operational non-loopback IPv4 interface.
        result.AddRange(DetectLocalNetworks().Select(n => (n.Network, n.Prefix)));
        if (result.Count == 0)
            throw new InvalidOperationException("No active IPv4 interface found; set Bamf:Subnets in appsettings.json");
        return result;
    }

    private static IPAddress GetNetworkAddress(IPAddress ip, int prefix)
    {
        var ipBytes = ip.GetAddressBytes();
        uint ipUint = (uint)(ipBytes[0] << 24 | ipBytes[1] << 16 | ipBytes[2] << 8 | ipBytes[3]);
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        uint net = ipUint & mask;
        return new IPAddress(new[] { (byte)(net >> 24), (byte)(net >> 16), (byte)(net >> 8), (byte)net });
    }

    private static bool InSubnet(IPAddress ip, IPAddress network, int prefix)
    {
        var a = ip.GetAddressBytes();
        var n = network.GetAddressBytes();
        uint ipU = (uint)(a[0] << 24 | a[1] << 16 | a[2] << 8 | a[3]);
        uint netU = (uint)(n[0] << 24 | n[1] << 16 | n[2] << 8 | n[3]);
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return (ipU & mask) == netU;
    }

    private static IEnumerable<IPAddress> EnumerateSubnet(IPAddress network, int prefix)
    {
        // Cap at /22 (1022 hosts) to keep sweeps sane.
        if (prefix < 22) prefix = 22;

        var n = network.GetAddressBytes();
        uint netU = (uint)(n[0] << 24 | n[1] << 16 | n[2] << 8 | n[3]);
        uint count = (uint)(1 << (32 - prefix));

        for (uint i = 1; i < count - 1; i++)
        {
            uint addr = netU + i;
            yield return new IPAddress(new[] { (byte)(addr >> 24), (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr });
        }
    }

    /// <summary>
    /// ICMP echo to each online, unignored device on the given networks, and
    /// the round-trip time recorded. A device that drops echoes is recorded
    /// as no reply, which the dashboard shows as such rather than as slow.
    /// </summary>
    private async Task ProbeLatency(IReadOnlyCollection<string> subnets, CancellationToken ct)
    {
        var targets = _store.GetAll()
            .Where(h => h.Online && !h.Ignored && !h.Forgotten && subnets.Contains(h.Subnet, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (targets.Count == 0) return;
        var samples = new System.Collections.Concurrent.ConcurrentBag<(long, int?)>();
        using var gate = new SemaphoreSlim(32);
        await Task.WhenAll(targets.Select(async h =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(h.Ip, 1000);
                samples.Add((h.Id, reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : null));
            }
            catch { samples.Add((h.Id, null)); }
            finally { gate.Release(); }
        }));
        _store.RecordLatency(samples.ToList());
    }

    /// <summary>
    /// A snooze ran out and the device ended up the other way round from when
    /// it started: the alert it would have had, saying so.
    /// </summary>
    public Task SnoozeEnded(HostRecord host, CancellationToken ct) => SendStatusAlert(host, host.Online, ct, snoozeOver: true);

    /// <summary>Alerts for a watched host going down or recovering. Held back while the device is snoozed.</summary>
    private async Task SendStatusAlert(HostRecord host, bool up, CancellationToken ct, bool snoozeOver = false)
    {
        if (!Takes("status")) return;

        var name = host.CustomName != "" ? host.CustomName
                 : (host.Hostname != "" ? host.Hostname : host.Mac);
        if (!snoozeOver && _store.IsSnoozed(host.Id))
        {
            _log.LogInformation("{Name} is snoozed: not sending its {State} alert", name, up ? "back online" : "offline");
            return;
        }

        // Compute downtime for recovery messages.
        string? downFor = null;
        if (up)
        {
            var lastOff = _store.LastOfflineAt(host.Id);
            if (lastOff is not null)
            {
                var span = DateTime.UtcNow - lastOff.Value;
                downFor = FormatSpan(span);
            }
        }

        var title = up ? $"{name} is back online" : $"{name} went offline";
        var text = up
            ? $"BAMF: {name} ({host.Ip}) is back online" + (downFor is not null ? $" after {downFor} down" : "")
            : $"BAMF: {name} ({host.Ip}) went offline";
        if (snoozeOver) text += up ? ", seen as its snooze ended" : ", and was still offline when its snooze ended";

        HttpRequestMessage Build(string url, string format)
        {
            var payload = "";
            if (format == "discord")
            {
                payload = JsonSerializer.Serialize(new
                {
                    username = "BAMF",
                    embeds = new[]
                    {
                        new
                        {
                            title = up ? $"✅ {name} is back online" : $"🔴 {name} went offline",
                            description = (up
                                ? (downFor is not null ? $"Recovered after {downFor} down." : "Recovered.")
                                : "A watched host stopped responding.") + (snoozeOver ? " Its snooze has just ended." : ""),
                            color = up ? 0x3FDB7F : 0xF2716F,
                            fields = new object[]
                            {
                                new { name = "IP",      value = $"`{host.Ip}`", inline = true },
                                new { name = "Network", value = host.Subnet,     inline = true },
                                new { name = "MAC",     value = $"`{host.Mac}`", inline = true },
                            },
                            timestamp = DateTime.UtcNow.ToString("o"),
                            footer = new { text = "BAMF watch alert" },
                        }
                    }
                });
            }
            var generic = JsonSerializer.Serialize(new { content = text, message = text, up, mac = host.Mac, ip = host.Ip });
            return BuildAlertRequest(url, format, title: title, message: text,
                priority: up ? 3 : 4, tags: up ? "green_circle" : "red_circle",
                discordPayload: payload, genericPayload: generic);
        }

        try { await Deliver("status", title, text, Build, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Watch alert failed for {Name}", name); }
    }
}
