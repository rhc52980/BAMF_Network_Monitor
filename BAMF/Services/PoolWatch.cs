using System.Net;
using System.Text.Json.Nodes;

namespace LanWatch.Services;

/// <summary>
/// DHCP pool pressure. A network that runs out of addresses stops handing them out, and the next device to join gets none, which looks
/// like "the Wi-Fi is broken". Each pool is a range of addresses a router hands out; BAMF counts the devices it has seen at an address
/// in the range over the last day (a lease outlasts the device being on, so a device that has just gone still holds its address), and
/// says how full that is. The range comes from a UniFi controller, which knows it, or you enter it from the router's settings, so it
/// works whatever the router is. Off until switched on; the alert at a percentage you choose is a second switch, also off.
/// </summary>
public sealed class PoolWatch : BackgroundService
{
    public sealed record Pool(string Network, string Name, string Start, string End, string Source);
    public sealed record Usage(Pool Pool, int Size, int Used, int Percent);

    public const int MaxPools = 16, DefaultPercent = 85;
    /// <summary>How long a device that has gone still counts as holding its address: a common lease time.</summary>
    public static readonly TimeSpan LeaseHeld = TimeSpan.FromHours(24);
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly NetworkGear _gear;
    private readonly ILogger<PoolWatch> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private List<Pool> _fromController = new();
    private string? _controllerError, _controllerAt;

    public PoolWatch(HostStore store, ScannerService scanner, NetworkGear gear, ILogger<PoolWatch> log)
    {
        _store = store; _scanner = scanner; _gear = gear; _log = log;
    }

    public bool Enabled => _store.GetSetting("poolOn") == "true";
    public bool AlertOn => Enabled && _store.GetSetting("poolAlert") == "true";
    public int Percent => int.TryParse(_store.GetSetting("poolPercent"), out var p) ? Math.Clamp(p, 50, 99) : DefaultPercent;
    public List<Pool> Manual => _store.GetJsonList<Pool>("poolManual");
    public string? ControllerError => _controllerError;
    public string? ControllerAt => _controllerAt;

    /// <summary>The pools you entered, made safe to keep, or an error to show.</summary>
    public static (List<Pool>? Pools, string? Error) Clean(IEnumerable<Pool>? pools)
    {
        var list = new List<Pool>();
        foreach (var p in pools ?? Array.Empty<Pool>())
        {
            if (ScannerService.CheckNetwork(p.Network, out var label) is { } why) return (null, why);
            if (!IPAddress.TryParse((p.Start ?? "").Trim(), out var a) || !IPAddress.TryParse((p.End ?? "").Trim(), out var b) ||
                a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || b.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return (null, "Give the first and last address the router hands out, like 192.168.1.100 and 192.168.1.250.");
            if (Number(a) > Number(b)) return (null, "The first address has to come before the last.");
            if (!ScannerService.NetworkContains(label, a.ToString()) || !ScannerService.NetworkContains(label, b.ToString()))
                return (null, $"Both addresses have to be inside {label}.");
            var name = (p.Name ?? "").Trim();
            list.Add(new Pool(label, name.Length > 40 ? name[..40] : name, a.ToString(), b.ToString(), "manual"));
        }
        if (list.Count > MaxPools) return (null, $"At most {MaxPools} pools.");
        if (list.GroupBy(p => p.Network).Any(g => g.Count() > 1)) return (null, "One pool for each network.");
        return (list, null);
    }

    private static uint Number(IPAddress ip) { var b = ip.GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }

    /// <summary>Every pool: the controller's, with yours replacing its entry for the same network.</summary>
    public List<Pool> All()
    {
        var manual = Manual;
        return manual.Concat(_fromController.Where(c => manual.All(m => m.Network != c.Network))).OrderBy(p => p.Network, StringComparer.Ordinal).ToList();
    }

    /// <summary>How full each pool is: devices seen in its range in the last day, over the size of the range. Pure, for the tests.</summary>
    public static List<Usage> Measure(IEnumerable<Pool> pools, IEnumerable<HostRecord> hosts, DateTime nowUtc)
    {
        var holding = hosts.Where(h => !h.Forgotten && !h.Ignored && IPAddress.TryParse(h.Ip, out _) &&
            (h.Online || (DateTime.TryParse(h.LastSeen, null, System.Globalization.DateTimeStyles.RoundtripKind, out var seen) && nowUtc - seen.ToUniversalTime() <= LeaseHeld)))
            .Select(h => Number(IPAddress.Parse(h.Ip))).ToList();
        var list = new List<Usage>();
        foreach (var p in pools)
        {
            var from = Number(IPAddress.Parse(p.Start)); var to = Number(IPAddress.Parse(p.End));
            var size = (int)Math.Min(int.MaxValue, (long)to - from + 1);
            var used = holding.Count(a => a >= from && a <= to);
            list.Add(new Usage(p, size, used, size == 0 ? 0 : (int)Math.Round(100.0 * used / size)));
        }
        return list;
    }

    public List<Usage> Usages() => Measure(All(), _store.GetAll(), DateTime.UtcNow);

    /// <summary>The ranges a UniFi controller says its networks hand out. Pure, for the tests.</summary>
    public static List<Pool> ParseNetworks(JsonNode? networks)
    {
        var list = new List<Pool>();
        foreach (var n in (networks?["data"] as JsonArray) ?? new JsonArray())
        {
            string S(string k) { try { return n?[k]?.GetValue<string>()?.Trim() ?? ""; } catch (InvalidOperationException) { return n?[k]?.ToString().Trim() ?? ""; } }
            bool On() { try { return n?["dhcpd_enabled"]?.GetValue<bool>() ?? false; } catch (InvalidOperationException) { return false; } }
            if (!On()) continue;
            if (!IPAddress.TryParse(S("dhcpd_start"), out var a) || !IPAddress.TryParse(S("dhcpd_stop"), out var b) || Number(a) > Number(b)) continue;
            if (ScannerService.CheckNetwork(S("ip_subnet"), out var label) is not null) continue;     // not a private network BAMF can watch
            if (!ScannerService.NetworkContains(label, a.ToString())) continue;
            var name = S("name");
            list.Add(new Pool(label, name.Length > 40 ? name[..40] : name, a.ToString(), b.ToString(), "unifi"));
        }
        return list;
    }

    /// <summary>Reads the controller's ranges now and looks over the pools. Returns how full each is.</summary>
    public async Task<List<Usage>> Run(CancellationToken ct)
    {
        await _busy.WaitAsync(ct);
        try
        {
            if (_gear.UniFiSettings() is { } uni)
            {
                try { _fromController = await _gear.Unifi.ReadNetworks(uni.Settings, ct); _controllerError = null; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _controllerError = ex is InvalidOperationException ? ex.Message : ex.GetBaseException().Message; }
                _controllerAt = DateTime.UtcNow.ToString("o");
            }
            else { _fromController = new(); _controllerError = null; _controllerAt = null; }

            var usages = Usages();
            if (AlertOn) await Alert(usages, ct);
            return usages;
        }
        finally { _busy.Release(); }
    }

    private async Task Alert(List<Usage> usages, CancellationToken ct)
    {
        var limit = Percent;
        var already = _store.GetJsonList<string>("poolAlerted").ToHashSet();
        var next = new HashSet<string>();
        foreach (var u in usages)
        {
            // Alerts once on crossing the limit, and again only after it has fallen five points below it.
            var held = already.Contains(u.Pool.Network);
            if (u.Percent >= limit || (held && u.Percent > limit - 5)) next.Add(u.Pool.Network);
            if (u.Percent < limit || held) continue;
            var left = Math.Max(0, u.Size - u.Used);
            var name = u.Pool.Name != "" ? $"{u.Pool.Name} ({u.Pool.Network})" : u.Pool.Network;
            var title = $"The address pool for {name} is {u.Percent}% used";
            var detail = $"{u.Used} of the {u.Size} addresses the router hands out on {u.Pool.Network} ({u.Pool.Start} to {u.Pool.End}) belong to devices seen in the last day: about {left} left. " +
                         "When it runs out, the next device to join gets no address and looks as if the network is broken. Widen the range or shorten the lease time in the router, " +
                         "or move some devices to a static address or another network.";
            _log.LogWarning("Pool: {Title}", title);
            _store.AddAlert("pool", title, detail);
            await _scanner.SendGenericAlert(title, detail, "status", ct);
        }
        _store.SetJsonList("poolAlerted", next);
    }

    /// <summary>Remembers the pools already over the limit as alerted, when the alert is switched on, so it speaks of what happens next.</summary>
    public void Baseline() => _store.SetJsonList("poolAlerted", Usages().Where(u => u.Percent >= Percent).Select(u => u.Pool.Network));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { if (Enabled) await Run(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "The address pool check failed"); }
            try { await Task.Delay(Every, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
