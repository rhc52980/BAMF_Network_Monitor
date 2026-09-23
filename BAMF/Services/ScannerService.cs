using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LanWatch.Services;

/// <summary>
/// Background loop: ping-sweeps the subnet to populate the ARP cache,
/// reads the ARP table, resolves hostnames + vendors, upserts into the
/// store, and fires a webhook when a brand-new MAC appears.
/// </summary>
public partial class ScannerService : BackgroundService
{
    private readonly HostStore _store;
    private readonly OuiLookup _oui;
    private readonly ILogger<ScannerService> _log;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly UpdateChecker _updates;

    /// <summary>
    /// Floor for any scan interval. Below this the sweep barely finishes before
    /// the next one starts, and the traffic stops being background noise.
    /// </summary>
    public const int MinIntervalSeconds = 5;

    public DateTime? LastScanUtc { get; private set; }

    /// <summary>Interval used by any network without its own override.</summary>
    public int ScanIntervalSeconds { get; private set; } = 60;

    /// <summary>Effective interval per network, after overrides are applied.</summary>
    public IReadOnlyDictionary<string, int> SubnetIntervals { get; private set; } =
        new Dictionary<string, int>();

    public IReadOnlyList<string> SubnetLabels { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Configured networks the dashboard has paused. Still listed in
    /// <see cref="SubnetLabels"/> - they remain configured, so their hosts keep
    /// their tab and last-known state - but never scanned and never judged
    /// offline. Only a label already in Bamf:Subnets can appear here, so the
    /// file stays the sole authority on which networks BAMF may touch at all.
    /// </summary>
    public IReadOnlySet<string> DisabledSubnets { get; private set; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> SubnetModes { get; private set; } =
        new Dictionary<string, string>();
    private bool _npcapWarned;
    private DateTime _lastPruneUtc = DateTime.MinValue;

    // Scans asked for from outside (an inbound webhook): the networks wanted,
    // "*" for all, and a way to wake the loop from its wait.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _requests = new();
    private CancellationTokenSource _wake = new();

    /// <summary>Makes a network (or every network) due now and wakes the loop.</summary>
    public void RequestScan(string? subnet)
    {
        _requests.Enqueue(string.IsNullOrWhiteSpace(subnet) ? "*" : subnet.Trim());
        try { _wake.Cancel(); } catch { }
    }

    /// <summary>When each network is next due, keyed by CIDR label.</summary>
    private readonly Dictionary<string, DateTime> _dueAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Snapshot of <see cref="_dueAt"/> for the API, replaced whole after each
    /// pass. The dictionary itself is only ever touched on the scan thread;
    /// readers get an immutable copy rather than a view of it mid-edit. Paused
    /// networks are absent - they have no next scan.
    /// </summary>
    public IReadOnlyDictionary<string, DateTime> SubnetNextDue { get; private set; } =
        new Dictionary<string, DateTime>();

    /// <summary>
    /// Where this machine sits on one configured network: its own address and
    /// MAC there (null when it has no interface on it), and the default gateway
    /// if one is on that network.
    /// </summary>
    public sealed record NetworkPlace(string? SelfIp, string? SelfMac, string? Gateway);

    private IReadOnlyDictionary<string, NetworkPlace>? _places;
    private DateTime _placesAt;
    private readonly object _placesLock = new();

    /// <summary>
    /// <see cref="NetworkPlace"/> for every configured network. These are the two
    /// things the dashboard's map can state rather than guess: ARP shows who is on
    /// a network, not how anything is cabled, but the machine does know its own
    /// address and its routing table does know the gateway. Cached for a minute,
    /// because enumerating interfaces isn't free and the dashboard asks every ten
    /// seconds.
    /// </summary>
    public IReadOnlyDictionary<string, NetworkPlace> NetworkPlaces()
    {
        lock (_placesLock)
        {
            if (_places is not null && DateTime.UtcNow - _placesAt < TimeSpan.FromMinutes(1))
                return _places;

            var gateways = PortChecker.DefaultGateways();
            var result = new Dictionary<string, NetworkPlace>(StringComparer.OrdinalIgnoreCase);
            foreach (var label in SubnetLabels)
            {
                var parts = label.Split('/');
                if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var net)
                    || !int.TryParse(parts[1], out var prefix)) continue;

                var local = FindLocalEndpoint(net, prefix);
                string? gateway = null;
                foreach (var g in gateways)
                    if (IPAddress.TryParse(g, out var a) && InSubnet(a, net, prefix)) { gateway = g; break; }

                result[label] = new NetworkPlace(
                    local?.Ip.ToString(),
                    local is null ? null : FormatMac(local.Value.Mac),
                    gateway);
            }
            _places = result;
            _placesAt = DateTime.UtcNow;
            return result;
        }
    }

    /// <summary>Override keys already warned about, so the log says it once.</summary>
    private readonly HashSet<string> _warnedIntervalKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Duplicate Bamf:Subnets entries already warned about, likewise.</summary>
    private readonly HashSet<string> _warnedDuplicateSubnets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Networks already warned about for having no local interface. The warning
    /// is worth reading once; repeated every pass it buries everything else in
    /// the log. Cleared for a network the moment it does get an interface, so
    /// losing it again warns again.
    /// </summary>
    private readonly HashSet<string> _warnedNoInterface = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Webhook endpoint. A URL saved from the dashboard wins over
    /// appsettings.json, matching how the other runtime settings behave, so
    /// changing it doesn't need a config edit or a restart.
    /// </summary>
    public string? WebhookUrl
    {
        get
        {
            var db = _store.GetSetting("webhookUrl");
            if (db is not null) return db.Length == 0 ? null : db;
            var cfg = _config["Bamf:WebhookUrl"];
            return string.IsNullOrWhiteSpace(cfg) ? null : cfg;
        }
    }

    /// <summary>
    /// How alerts are delivered to the webhook URL. "auto" keeps the original
    /// behaviour: a Discord URL gets a rich embed, anything else a generic JSON
    /// body. "ntfy" posts plain text with Title/Priority/Tags headers the way
    /// ntfy expects; "gotify" posts its {title, message, priority} JSON; "json"
    /// forces the generic body even for a Discord URL. Dashboard value wins over
    /// appsettings.json, like the URL itself.
    /// </summary>
    public string WebhookFormat
    {
        get
        {
            var v = (_store.GetSetting("webhookFormat") ?? _config["Bamf:WebhookFormat"] ?? "auto")
                .Trim().ToLowerInvariant();
            return v is "ntfy" or "gotify" or "json" or "discord" ? v : "auto";
        }
    }

    private static bool IsDiscordUrl(string url) =>
        url.Contains("discord.com/api/webhooks", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("discordapp.com/api/webhooks", StringComparison.OrdinalIgnoreCase);

    /// <summary>The concrete format to use for a URL once "auto" is resolved.</summary>
    private static string ResolveFormat(string url, string format) =>
        format == "auto" ? (IsDiscordUrl(url) ? "discord" : "json") : format;

    /// <summary>
    /// One alert as an HTTP request in the resolved format. ntfy carries the
    /// title in a header, and headers are ASCII, so the title is stripped to
    /// ASCII and the device name - which may not be - lives in the body.
    /// Priority is on ntfy's 1-5 scale; Gotify's 0-10 gets double.
    /// </summary>
    private static HttpRequestMessage BuildAlertRequest(string url, string format, string title, string message,
        int priority, string tags, string discordPayload, string genericPayload)
    {
        switch (format)
        {
            case "ntfy":
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(message, Encoding.UTF8, "text/plain"),
                };
                var ascii = new string(title.Where(c => c < 128).ToArray()).Trim();
                if (ascii != "") req.Headers.TryAddWithoutValidation("Title", ascii);
                req.Headers.TryAddWithoutValidation("Priority", priority.ToString());
                if (tags != "") req.Headers.TryAddWithoutValidation("Tags", tags);
                return req;
            }
            case "gotify":
                return new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { title, message, priority = priority * 2 }),
                        Encoding.UTF8, "application/json"),
                };
            case "discord":
                return new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(discordPayload, Encoding.UTF8, "application/json"),
                };
            default:
                return new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(genericPayload, Encoding.UTF8, "application/json"),
                };
        }
    }

    /// <summary>Effective toggle: DB override wins, else appsettings default.</summary>
    /// <summary>
    /// One ICMP echo to each online device after its network is scanned, for
    /// the latency column and sparkline. Dashboard setting wins over the file.
    /// </summary>
    public bool LatencyProbeEnabled
    {
        get
        {
            var db = _store.GetSetting("latencyProbe");
            if (db is not null) return db == "true";
            return _config.GetValue("Bamf:LatencyProbe", true);
        }
    }

    public bool ActiveArpEnabled
    {
        get
        {
            var db = _store.GetSetting("activeArpScan");
            if (db is not null) return db == "true";
            return _config.GetValue("Bamf:ActiveArpScan", false);
        }
    }

    /// <summary>
    /// Default interval, dashboard value winning over appsettings.json. Same
    /// precedence as <see cref="ActiveArpEnabled"/>: a setting saved from the UI
    /// lives in the database, and the file supplies the default it started from.
    /// </summary>
    public int ConfiguredDefaultInterval
    {
        get
        {
            var db = _store.GetSetting("scanIntervalSeconds");
            if (db is not null && int.TryParse(db, out var secs))
                return Math.Max(MinIntervalSeconds, secs);
            return Math.Max(MinIntervalSeconds, _config.GetValue("Bamf:ScanIntervalSeconds", 60));
        }
    }

    /// <summary>
    /// Consecutive scans of its network a host may be missed by before it is
    /// declared offline. 1 reproduces the old single-miss behaviour. Dashboard
    /// value wins over appsettings.json, same as the other scan settings.
    /// </summary>
    public int ConfiguredOfflineMisses
    {
        get
        {
            var db = _store.GetSetting("offlineAfterMissedScans");
            if (db is not null && int.TryParse(db, out var n))
                return Math.Clamp(n, 1, 20);
            return Math.Clamp(_config.GetValue("Bamf:OfflineAfterMissedScans", 2), 1, 20);
        }
    }

    /// <summary>Probe concurrency, dashboard value winning over appsettings.json.</summary>
    public int ConfiguredConcurrency
    {
        get
        {
            var db = _store.GetSetting("pingConcurrency");
            if (db is not null && int.TryParse(db, out var n))
                return Math.Clamp(n, 1, 1024);
            return Math.Clamp(_config.GetValue("Bamf:PingConcurrency", 64), 1, 1024);
        }
    }

    public bool NpcapAvailable => ArpScanner.IsAvailable;

    /// <summary>Effective toggle: DB override wins, else appsettings default.</summary>
    public bool AutoIgnoreRandomEnabled
    {
        get
        {
            var db = _store.GetSetting("autoIgnoreRandomizedMacs");
            if (db is not null) return db == "true";
            return _config.GetValue("Bamf:AutoIgnoreRandomizedMacs", false);
        }
    }

    private readonly MdnsListener _mdns;

    /// <summary>The mDNS listener, for status in the API.</summary>
    public MdnsListener Mdns => _mdns;

    /// <summary>Whether to listen for mDNS announcements; dashboard value wins over appsettings.json.</summary>
    public bool MdnsEnabled
    {
        get
        {
            var db = _store.GetSetting("mdnsListen");
            if (db is not null) return db == "true";
            return _config.GetValue("Bamf:MdnsListen", true);
        }
    }

    private readonly TrafficMonitor _traffic;
    public TrafficMonitor Traffic => _traffic;

    /// <summary>
    /// Bytes per device and DHCP/DNS watching, through Npcap. Dashboard
    /// setting wins over the file; only takes effect when Npcap is installed.
    /// </summary>
    public bool TrafficMonitorEnabled
    {
        get
        {
            var db = _store.GetSetting("trafficMonitor");
            if (db is not null) return db == "true";
            return _config.GetValue("Bamf:TrafficMonitor", true);
        }
    }

    public ScannerService(HostStore store, OuiLookup oui, IConfiguration config,
        IHttpClientFactory httpFactory, ILogger<ScannerService> log, UpdateChecker updates,
        MdnsListener mdns, TrafficMonitor traffic)
    {
        _store = store;
        _oui = oui;
        _config = config;
        _httpFactory = httpFactory;
        _log = log;
        _updates = updates;
        _mdns = mdns;
        _traffic = traffic;
        _traffic.OnAlert = a => SendWatchAlert(a, CancellationToken.None);
        _traffic.OnArp = OnArpSeen;
        _traffic.OnNdp = OnNdpSeen;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Re-read each pass so a change from the dashboard or an edit to
                // appsettings.json takes effect without a restart, the same as
                // the subnet list already does.
                ScanIntervalSeconds = ConfiguredDefaultInterval;
                var concurrency = ConfiguredConcurrency;
                var overrides = ReadIntervalOverrides();

                // Receive-only; starts or stops to follow the setting each pass.
                _mdns.EnsureRunning(MdnsEnabled);

                var subnets = ResolveSubnets();
                // Likewise the traffic monitor, on the interfaces that own the configured networks.
                _traffic.EnsureRunning(TrafficMonitorEnabled && ArpScanner.IsAvailable,
                    subnets.Select(s => FindLocalEndpoint(s.Network, s.Prefix)?.Ip).Where(ip => ip is not null).Select(ip => ip!).ToList());
                var labels = subnets.Select(s => $"{s.Network}/{s.Prefix}").ToList();
                SubnetLabels = labels;
                // Built by hand rather than ToDictionary: a duplicate label would
                // throw there and take the whole pass with it. ResolveSubnets now
                // de-duplicates, but a table that survives one anyway is cheaper
                // than a scanner that stops.
                var intervals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in labels)
                    intervals[l] = overrides.TryGetValue(l, out var secs) ? secs : ScanIntervalSeconds;
                SubnetIntervals = intervals;
                WarnAboutUnmatchedOverrides(overrides, labels);

                // Paused networks: only labels that are actually configured count,
                // so a stale entry for a removed network is simply ignored.
                var disabled = new HashSet<string>(
                    ReadDisabledSubnets().Where(d => labels.Contains(d, StringComparer.OrdinalIgnoreCase)),
                    StringComparer.OrdinalIgnoreCase);
                DisabledSubnets = disabled;

                // Forget schedules for networks that are no longer configured, and
                // for paused ones - so the loop doesn't wake on their account, and
                // so re-enabling one makes it due straight away rather than at
                // whatever time was pencilled in before it was paused.
                foreach (var gone in _dueAt.Keys.Where(k => !labels.Contains(k) || disabled.Contains(k)).ToList())
                    _dueAt.Remove(gone);
                // A network that has never run yet is due now; say so rather
                // than leaving it out, so the dashboard can count down to it.
                foreach (var l in labels)
                    if (!disabled.Contains(l) && !_dueAt.ContainsKey(l)) _dueAt[l] = DateTime.UtcNow;
                // Anything asked for from outside is due now.
                while (_requests.TryDequeue(out var wanted))
                    foreach (var l in labels)
                        if (!disabled.Contains(l) && (wanted == "*" || string.Equals(wanted, l, StringComparison.OrdinalIgnoreCase)))
                            _dueAt[l] = DateTime.UtcNow;
                SubnetNextDue = new Dictionary<string, DateTime>(_dueAt, StringComparer.OrdinalIgnoreCase);

                // Carry modes forward, mark paused networks as such, and drop any
                // network that has left the configuration.
                var modes = new Dictionary<string, string>(SubnetModes);
                foreach (var stale in modes.Keys.Where(k => !labels.Contains(k)).ToList())
                    modes.Remove(stale);
                foreach (var d in disabled) modes[d] = "paused";
                SubnetModes = modes;

                var startedAt = DateTime.UtcNow;
                var due = subnets
                    .Where(s => !disabled.Contains($"{s.Network}/{s.Prefix}"))
                    .Where(s => !_dueAt.TryGetValue($"{s.Network}/{s.Prefix}", out var at) || at <= startedAt)
                    .ToList();

                if (due.Count > 0)
                {
                    var activeArpWanted = ActiveArpEnabled;
                    if (activeArpWanted && !ArpScanner.IsAvailable && !_npcapWarned)
                    {
                        _npcapWarned = true;
                        _log.LogWarning("ActiveArpScan is enabled but the Npcap driver was not found " +
                                        "(https://npcap.com). Falling back to ping sweep.");
                    }

                    var seenMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var passStart = DateTime.UtcNow;
                    var covered = new List<string>();
                    foreach (var (network, prefix) in due)
                    {
                        ct.ThrowIfCancellationRequested();
                        var label = $"{network}/{prefix}";
                        var mode = await RunScan(network, prefix, concurrency,
                            activeArpWanted, seenMacs, ct);
                        modes[label] = mode;
                        covered.Add(label);
                        _dueAt[label] = DateTime.UtcNow.AddSeconds(SubnetIntervals[label]);
                    }
                    SubnetModes = modes;
                    SubnetNextDue = new Dictionary<string, DateTime>(_dueAt, StringComparer.OrdinalIgnoreCase);

                    // Only judge the networks this pass actually visited. When every
                    // network is on the same interval a pass covers them all and this
                    // is the original whole-database sweep; when intervals differ, a
                    // pass that skipped a network must not declare its hosts down.
                    // A paused network is never covered, so while one is paused this
                    // always takes the scoped path and its hosts keep their last
                    // known state instead of being declared offline unlooked-at.
                    // Hosts on a network that has been removed from the config
                    // are judged on every pass regardless: no pass can ever see
                    // them again, and before this they only went offline on the
                    // full sweep, which never comes while anything is paused.
                    var wentDown = _store.MarkOffline(seenMacs,
                        covered.Count == labels.Count ? null : covered,
                        ConfiguredOfflineMisses,
                        labels);
                    foreach (var h in wentDown)
                        await SendStatusAlert(h, up: false, CancellationToken.None);

                    // The same judgement per address, for devices answering on
                    // several: an address that stopped answering is retired, and a
                    // device whose main address went moves to one still answering.
                    _store.SweepAddresses(passStart,
                        covered.Count == labels.Count ? null : covered,
                        ConfiguredOfflineMisses,
                        labels);

                    var recovered = _store.DrainRecovered();
                    foreach (var h in recovered)
                        await SendStatusAlert(h, up: true, CancellationToken.None);

                    // Round-trip time to every device still online on the networks
                    // this pass covered: one echo each, a few dozen at a time.
                    if (LatencyProbeEnabled) await ProbeLatency(covered, ct);

                    // Vendor/hostname-derived device guesses. No packets are sent -
                    // deeper fingerprinting stays behind the Identify action.
                    _store.ApplyPassiveFingerprints();

                    // What devices said about themselves since the last pass. Written
                    // here, on the scan thread, rather than from the receive loop, so
                    // the database sees one writer.
                    var learned = 0;
                    foreach (var o in _mdns.Drain())
                        if (_store.ApplyMdns(o.Ip, o.Name, o.Services)) learned++;
                    if (learned > 0)
                        _log.LogInformation("mDNS: learned names or services for {Count} device(s)", learned);

                    // IPv6 addresses: what the traffic monitor saw, and every five
                    // minutes the neighbour table. Same thread, same one writer.
                    try { await WatchIpv6(ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { _log.LogWarning(ex, "IPv6 watch failed"); }

                    LastScanUtc = DateTime.UtcNow;
                }

                // Opt-in, at most once a day, and failures are silent.
                await _updates.MaybeCheckAsync(ct);

                if ((DateTime.UtcNow - _lastPruneUtc).TotalHours >= 24)
                {
                    _store.PruneEvents();
                    _lastPruneUtc = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Scan failed");
            }

            try
            {
                _wake = new CancellationTokenSource();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _wake.Token);
                await Task.Delay(WaitUntilNextDue(), linked.Token);
            }
            catch (OperationCanceledException) { if (ct.IsCancellationRequested) break; }
        }
    }

    /// <summary>
    /// Sleep until the soonest-due network, floored so a misconfigured interval
    /// can't spin and capped so an edit to appsettings.json is picked up within
    /// a minute even when every network is on a long interval.
    /// </summary>
    private TimeSpan WaitUntilNextDue()
    {
        var next = _dueAt.Count > 0
            ? _dueAt.Values.Min()
            : DateTime.UtcNow.AddSeconds(ScanIntervalSeconds);
        var wait = next - DateTime.UtcNow;
        if (wait < TimeSpan.FromSeconds(1)) return TimeSpan.FromSeconds(1);
        if (wait > TimeSpan.FromSeconds(60)) return TimeSpan.FromSeconds(60);
        return wait;
    }

    /// <summary>
    /// Per-network interval overrides, keyed by the subnet's CIDR label. A map
    /// saved from the dashboard replaces the appsettings.json section outright
    /// rather than merging with it, so removing a row in the UI actually removes
    /// the override instead of falling back to a stale file entry.
    /// </summary>
    public Dictionary<string, int> ReadIntervalOverrides()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var db = _store.GetSetting("subnetScanIntervalSeconds");
        if (db is not null)
        {
            try
            {
                var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(db);
                if (saved is not null)
                {
                    foreach (var (k, v) in saved) map[k] = Math.Max(MinIntervalSeconds, v);
                    return map;
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _log.LogWarning(ex, "Saved per-network intervals could not be read; " +
                    "falling back to appsettings.json.");
            }
        }

        foreach (var child in _config.GetSection("Bamf:SubnetScanIntervalSeconds").GetChildren())
        {
            if (int.TryParse(child.Value, out var secs))
                map[child.Key] = Math.Max(MinIntervalSeconds, secs);
            else
                _log.LogWarning("Ignoring SubnetScanIntervalSeconds entry for {Key}: " +
                    "'{Value}' is not a whole number of seconds.", child.Key, child.Value);
        }
        return map;
    }

    /// <summary>
    /// Networks the dashboard has paused, as saved. Filtered against the
    /// configured list by the caller; anything else here is ignored.
    /// </summary>
    public HashSet<string> ReadDisabledSubnets()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var db = _store.GetSetting("disabledSubnets");
        if (db is null) return set;
        try
        {
            var saved = System.Text.Json.JsonSerializer.Deserialize<List<string>>(db);
            if (saved is not null)
                foreach (var s in saved)
                    if (!string.IsNullOrWhiteSpace(s)) set.Add(s.Trim());
        }
        catch (System.Text.Json.JsonException ex)
        {
            _log.LogWarning(ex, "Saved paused-network list could not be read; treating every network as active.");
        }
        return set;
    }

    /// <summary>
    /// An override keyed to a network that isn't configured silently does nothing,
    /// which looks identical to the feature not working. Say so once per key.
    /// </summary>
    private void WarnAboutUnmatchedOverrides(Dictionary<string, int> overrides, List<string> labels)
    {
        foreach (var key in overrides.Keys)
        {
            if (labels.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            if (!_warnedIntervalKeys.Add(key)) continue;
            _log.LogWarning("SubnetScanIntervalSeconds has an entry for {Key}, which is not in " +
                "Bamf:Subnets - it will have no effect. Configured networks: {Configured}",
                key, labels.Count == 0 ? "(none)" : string.Join(", ", labels));
        }
    }

    private async Task<string> RunScan(IPAddress network, int prefix, int concurrency,
        bool activeArpWanted, HashSet<string> seenMacs, CancellationToken ct)
    {
        var subnetLabel = $"{network}/{prefix}";

        // Discovery works by reading the OS ARP table, which only ever holds
        // entries for subnets this machine has an interface on. For anything
        // else the probes are routed via the default gateway, the ARP table
        // stays empty, and the scan reports zero hosts no matter what is out
        // there. Probing it anyway would send a full sweep through - and at -
        // the router every cycle, forever, for a result that cannot work.
        var local = FindLocalEndpoint(network, prefix);
        if (local is null)
        {
            if (_warnedNoInterface.Add(subnetLabel))
                _log.LogWarning("Skipping {Subnet}: no local interface on this network, so its hosts " +
                    "can never appear in the ARP table. Remove it from Bamf:Subnets, or add a NIC on " +
                    "this network to scan it. (Reported once; the dashboard marks the network too.)",
                    subnetLabel);
            return "skipped";
        }
        if (_warnedNoInterface.Remove(subnetLabel))
            _log.LogInformation("{Subnet} now has a local interface; scanning it.", subnetLabel);

        _log.LogInformation("Subnet {Subnet}: scanning from local {Ip}", subnetLabel, local.Value.Ip);

        var addresses = EnumerateSubnet(network, prefix).ToList();

        List<(IPAddress Ip, string Mac)> arpEntries;
        string mode;

        if (activeArpWanted && ArpScanner.IsAvailable && local is not null)
        {
            try
            {
                _log.LogInformation("Scanning {Subnet} via active ARP ({Count} addresses)", subnetLabel, addresses.Count);
                arpEntries = await ArpScanner.ScanAsync(addresses, local.Value.Ip, local.Value.Mac, 1500, ct);
                // Include this machine itself (it doesn't answer its own ARP).
                arpEntries.Add((local.Value.Ip, FormatMac(local.Value.Mac)));
                mode = "active ARP";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Active ARP scan failed on {Subnet}; falling back to ping sweep", subnetLabel);
                arpEntries = await PingSweepDiscovery(network, prefix, addresses, concurrency, ct);
                mode = "ping sweep";
            }
        }
        else
        {
            _log.LogInformation("Scanning {Subnet} via ping sweep ({Count} addresses)", subnetLabel, addresses.Count);
            arpEntries = await PingSweepDiscovery(network, prefix, addresses, concurrency, ct);
            mode = "ping sweep";
        }

        // Filter to subnet and dedupe. One MAC can answer on several addresses
        // (a router, a server with a second IP), so every address is kept; in
        // address order, so a new device's lowest address becomes its main one.
        arpEntries = arpEntries
            .Where(e => InSubnet(e.Ip, network, prefix))
            .DistinctBy(e => (e.Mac.ToUpperInvariant(), e.Ip))
            .OrderBy(e => e.Ip.GetAddressBytes(), Comparer<byte[]>.Create((x, y) =>
            {
                for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
                    if (x[i] != y[i]) return x[i].CompareTo(y[i]);
                return x.Length.CompareTo(y.Length);
            }))
            .ToList();

        _log.LogInformation("{Mode} on {Subnet} found {Count} hosts", mode, subnetLabel,
            arpEntries.Select(e => e.Mac).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Two devices on one address, or the gateway's MAC changing.
        try { await CheckArp(subnetLabel, arpEntries, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.LogWarning(ex, "ARP watch failed on {Subnet}", subnetLabel); }

        // Resolve + upsert.
        var autoIgnoreRandom = AutoIgnoreRandomEnabled;
        foreach (var (ip, mac) in arpEntries)
        {
            ct.ThrowIfCancellationRequested();
            seenMacs.Add(mac);

            var hostname = await ResolveHostname(ip);
            var vendor = _oui.Lookup(mac);
            var isRandomized = vendor == "(randomized MAC)";

            var (isNew, ignored) = _store.UpsertSeen(mac, ip.ToString(), hostname, vendor, subnetLabel,
                autoIgnore: autoIgnoreRandom && isRandomized);
            if (isNew && !ignored)
            {
                _log.LogWarning("NEW HOST on {Subnet}: {Mac} at {Ip} ({Hostname}) [{Vendor}]",
                    subnetLabel, mac, ip, hostname, vendor);
                await Notify(mac, ip.ToString(), hostname, vendor, subnetLabel, ct);
            }
            else if (isNew)
            {
                _log.LogInformation("New randomized-MAC host auto-ignored on {Subnet}: {Mac} at {Ip}",
                    subnetLabel, mac, ip);
            }
        }

        return mode;
    }

    /// <summary>
    /// Fallback discovery: parallel ping sweep to populate the ARP cache,
    /// then read the OS ARP table.
    /// </summary>
    private async Task<List<(IPAddress Ip, string Mac)>> PingSweepDiscovery(
        IPAddress network, int prefix, List<IPAddress> addresses,
        int concurrency, CancellationToken ct)
    {
        // Bind the sweep to the NIC that actually owns this subnet. Without this,
        // on a multi-homed host the OS routing table may send pings for a
        // secondary subnet out the wrong interface, so their ARP entries never
        // populate and every host on that subnet looks offline.
        var localEndpoint = FindLocalEndpoint(network, prefix);

        // Callers are expected to have skipped subnets with no local interface.
        // Guard anyway: probing one from here would route every packet at the
        // gateway to populate an ARP table that cannot hold the answers.
        if (localEndpoint is null)
        {
            _log.LogWarning("Refusing to sweep {Network}/{Prefix}: no local interface on this network.",
                network, prefix);
            return new List<(IPAddress, string)>();
        }

        using var semaphore = new SemaphoreSlim(concurrency);
        var sweep = addresses.Select(async ip =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                // Bound UDP send forces ARP resolution out the correct NIC.
                await ProbeBound(localEndpoint.Value.Ip, ip, ct);
            }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(sweep);

        // Give the kernel a moment to move freshly-triggered ARP entries from
        // "incomplete" to "complete" before we read the table.
        await Task.Delay(400, ct);

        return await ReadArpTable(network, prefix);
    }

    /// <summary>
    /// Sends a bound UDP datagram from the given local subnet IP, forcing the
    /// kernel to ARP-resolve the target on the correct interface even on a
    /// multi-homed host. We don't care about a reply — the send is what triggers
    /// the ARP request whose result lands in the table.
    /// </summary>
    private static async Task ProbeBound(IPAddress localIp, IPAddress target, CancellationToken ct)
    {
        try
        {
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(localIp, 0));
            await udp.SendToAsync(new byte[] { 0 }, SocketFlags.None, new IPEndPoint(target, 9), ct);
        }
        catch { /* send failures are fine; some targets just won't resolve */ }
    }

    /// <summary>The server's own IP + MAC on the given subnet, if any.</summary>
    private static (IPAddress Ip, PhysicalAddress Mac)? FindLocalEndpoint(IPAddress network, int prefix)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (!InSubnet(addr.Address, network, prefix)) continue;
                var mac = nic.GetPhysicalAddress();
                if (mac.GetAddressBytes().Length == 6)
                    return (addr.Address, mac);
            }
        }
        return null;
    }

    private static string FormatMac(PhysicalAddress mac) =>
        string.Join(":", mac.GetAddressBytes().Select(b => b.ToString("X2")));

    // ---------------- ARP ----------------

    private async Task<List<(IPAddress Ip, string Mac)>> ReadArpTable(IPAddress network, int prefix)
    {
        var raw = OperatingSystem.IsWindows()
            ? await ReadArpTableWindows()
            : ReadArpTableLinux();

        var results = new List<(IPAddress, string)>();
        foreach (var (ip, mac) in raw)
        {
            // Skip broadcast/multicast pseudo-entries.
            if (mac is "FF:FF:FF:FF:FF:FF") continue;
            if (mac.StartsWith("01:00:5E") || mac.StartsWith("33:33")) continue;
            if (mac is "00:00:00:00:00:00") continue;

            // Only keep hosts inside the target subnet.
            if (!InSubnet(ip, network, prefix)) continue;

            results.Add((ip, mac));
        }

        // Include this machine itself (it never appears in its own ARP table).
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (!InSubnet(addr.Address, network, prefix)) continue;
                var mac = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                if (mac.Length == 17)
                    results.Add((addr.Address, mac));
            }
        }

        return results
            .GroupBy(r => r.Item2)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>Windows: parse `arp -a` output, dynamic entries only.</summary>
    private static async Task<List<(IPAddress Ip, string Mac)>> ReadArpTableWindows()
    {
        var results = new List<(IPAddress, string)>();

        var psi = new ProcessStartInfo
        {
            FileName = "arp",
            Arguments = "-a",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.ASCII,
        };
        using var proc = Process.Start(psi);
        if (proc is null) return results;
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();

        // Lines look like:  192.168.1.10          00-11-32-9f-44-2a     dynamic
        foreach (Match m in ArpLine().Matches(output))
        {
            if (!IPAddress.TryParse(m.Groups[1].Value, out var ip)) continue;
            if (!m.Groups[3].Value.Contains("dynamic", StringComparison.OrdinalIgnoreCase)) continue;
            results.Add((ip, m.Groups[2].Value.Replace('-', ':').ToUpperInvariant()));
        }
        return results;
    }

    /// <summary>Linux: parse /proc/net/arp. Flag 0x2 = complete entry.</summary>
    private static List<(IPAddress Ip, string Mac)> ReadArpTableLinux()
    {
        var results = new List<(IPAddress, string)>();
        const string path = "/proc/net/arp";
        if (!File.Exists(path)) return results;

        // Columns: IP address  HW type  Flags  HW address  Mask  Device
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            if (!IPAddress.TryParse(parts[0], out var ip)) continue;

            int flags;
            try { flags = Convert.ToInt32(parts[2], 16); }
            catch { continue; }
            if ((flags & 0x2) == 0) continue; // incomplete entry

            results.Add((ip, parts[3].ToUpperInvariant()));
        }
        return results;
    }

    [GeneratedRegex(@"^\s*(\d{1,3}(?:\.\d{1,3}){3})\s+([0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex ArpLine();

    // ---------------- helpers ----------------

    private static async Task<string> ResolveHostname(IPAddress ip)
    {
        // 1. Reverse DNS first. Filtered the same way NetBIOS answers are: a
        //    printer with no real name hands its MAC-derived one to DHCP, the
        //    gateway registers it, and it comes back from here looking official.
        //    Fall through to NetBIOS rather than returning "", in case that
        //    path has something better; if not, the caller keeps what it had.
        try
        {
            var task = Dns.GetHostEntryAsync(ip);
            var done = await Task.WhenAny(task, Task.Delay(1500));
            if (done == task && task.IsCompletedSuccessfully)
            {
                var dnsName = task.Result.HostName;
                if (!string.IsNullOrEmpty(dnsName) && !NetBiosResolver.LooksMacDerived(dnsName))
                    return dnsName;
            }
        }
        catch { }

        // 2. NetBIOS fallback (UDP 137) for the many devices without a PTR record.
        try
        {
            var name = await NetBiosResolver.QueryAsync(ip, 700, CancellationToken.None);
            if (!string.IsNullOrEmpty(name)) return name;
        }
        catch { }

        return "";
    }

    private List<(IPAddress Network, int Prefix)> ResolveSubnets()
    {
        var result = new List<(IPAddress, int)>();

        // Preferred: list of CIDRs in Bamf:Subnets.
        // Blank entries are skipped: the Home Assistant add-on blanks the
        // file's list past its own, and all blank means auto-detect.
        var configured = (_config.GetSection("Bamf:Subnets").Get<string[]>() ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c)).ToArray();

        // Back-compat: single Bamf:Subnet string.
        var single = _config["Bamf:Subnet"];
        if (configured.Length == 0 && !string.IsNullOrWhiteSpace(single))
            configured = new[] { single };

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
                    result.Add((network, prefix));
            }
        }
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

    // ---------------- quiet hours ----------------

    /// <summary>Quiet hours, local time, from the settings table; empty means none.</summary>
    public (string From, string To) QuietHours => (_store.GetSetting("quietFrom") ?? "", _store.GetSetting("quietTo") ?? "");
    public bool QuietDigest => _store.GetSetting("quietDigest") != "false";

    public bool IsQuietNow()
    {
        var (from, to) = QuietHours;
        if (!TimeOnly.TryParse(from, out var f) || !TimeOnly.TryParse(to, out var t) || f == t) return false;
        var now = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.Local));
        return f <= t ? now >= f && now < t : now >= f || now < t;
    }

    private readonly object _heldLock = new();
    private List<HeldAlert>? _held;
    // Kind is which kind of alert it was, so the digest goes to each destination
    // with just its own kinds. Alerts held before destinations had kinds have
    // none, and go to every destination.
    private sealed record HeldAlert(string At, string Title, string Text, string? Kind = null);
    private List<HeldAlert> Held
    {
        get
        {
            if (_held is null)
            {
                try { _held = JsonSerializer.Deserialize<List<HeldAlert>>(_store.GetSetting("heldAlerts") ?? "[]") ?? new(); }
                catch { _held = new(); }
            }
            return _held;
        }
    }
    public int HeldCount { get { lock (_heldLock) return Held.Count; } }

    /// <summary>Keeps an alert for the digest at the end of quiet hours.</summary>
    private void Hold(string kind, string title, string text)
    {
        lock (_heldLock)
        {
            Held.Add(new HeldAlert(DateTime.UtcNow.ToString("o"), title, text, kind));
            if (Held.Count > 100) Held.RemoveAt(0);
            _store.SetSetting("heldAlerts", JsonSerializer.Serialize(Held));
        }
        _log.LogInformation("Quiet hours: held \"{Title}\"", title);
    }

    /// <summary>
    /// What was held during quiet hours, as one message, once they end: to each
    /// destination, with the held alerts of the kinds it takes.
    /// </summary>
    public async Task<int> FlushHeldAlerts(CancellationToken ct)
    {
        List<HeldAlert> items;
        lock (_heldLock)
        {
            items = Held.ToList();
            Held.Clear();
            _store.SetSetting("heldAlerts", "[]");
        }
        if (items.Count == 0 || !QuietDigest) return items.Count;
        foreach (var d in Destinations())
        {
            var mine = items.Where(i => i.Kind is null || d.Kinds.Contains(i.Kind)).ToList();
            if (mine.Count == 0) continue;
            var fields = mine.Take(15).Select(i => (TimeZoneInfo.ConvertTimeFromUtc(DateTime.Parse(i.At, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal), TimeZoneInfo.Local).ToString("HH:mm") + " " + i.Title, i.Text)).ToList();
            if (mine.Count > 15) fields.Add(("And more", $"{mine.Count - 15} more alert(s) were held."));
            var text = string.Join("\n", mine.Select(i => "- " + i.Title));
            await SendReport($"While it was quiet: {mine.Count} alert{(mine.Count == 1 ? "" : "s")}", text, fields, ct, only: d.Id);
        }
        return items.Count;
    }

    /// <summary>How each kind of alert looks: its icon, colour, footer, ntfy tag and priority.</summary>
    private static (string Icon, int Color, string Footer, string Tag, int Priority) AlertStyle(string kind) => kind switch
    {
        "port" => ("\U0001F513 ", 0xE0A040, "BAMF port watch", "unlock", 4),
        "security" => ("\U0001F6E1 ", 0xE8483B, "BAMF security watch", "shield", 5),
        "internet" => ("\U0001F310 ", 0x4FB3D9, "BAMF internet watch", "globe_with_meridians", 4),
        "cert" => ("\U0001F510 ", 0xE0A040, "BAMF certificate watch", "lock", 4),
        _ => ("⏰ ", 0xB58AF0, "BAMF alert rule", "alarm_clock", 4),
    };

    /// <summary>Which kind of alert, for choosing destinations, each style of alert is.</summary>
    private static string KindOf(string style) => style switch
    {
        "rule" => "status",
        "internet" => "internet",
        _ => "security",   // security, port, cert
    };

    /// <summary>A rule, port, security, certificate or internet alert: title and detail, through the quiet-hours gate.</summary>
    public async Task SendGenericAlert(string title, string detail, string kind, CancellationToken ct)
    {
        var style = AlertStyle(kind);
        var text = $"BAMF: {title}. {detail}";
        HttpRequestMessage Build(string url, string format)
        {
            var payload = format == "discord" ? JsonSerializer.Serialize(new
            {
                username = "BAMF",
                embeds = new[] { new { title = style.Icon + title, description = detail, color = style.Color,
                    timestamp = DateTime.UtcNow.ToString("o"), footer = new { text = style.Footer } } },
            }) : "";
            var generic = JsonSerializer.Serialize(new { content = text, message = text, kind, title, detail });
            return BuildAlertRequest(url, format, title: title, message: detail, priority: style.Priority, tags: style.Tag,
                discordPayload: payload, genericPayload: generic);
        }
        try { await Deliver(KindOf(kind), title, text, Build, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Alert failed"); }
    }

    /// <summary>
    /// A scheduled report: a titled block of text, as an embed with fields on
    /// Discord and as text elsewhere, to every destination that takes reports
    /// (or just the one named). True when one of them accepted it. A report's
    /// hour is the user's own choice, so quiet hours don't hold it.
    /// </summary>
    public async Task<bool> SendReport(string title, string text, IReadOnlyList<(string Name, string Value)> fields, CancellationToken ct, string? only = null)
    {
        HttpRequestMessage Build(string url, string format)
        {
            var payload = format == "discord" ? JsonSerializer.Serialize(new
            {
                username = "BAMF",
                embeds = new[] { new { title, color = 0x4FB3D9, fields = fields.Select(f => new { name = f.Name, value = f.Value.Length > 1000 ? f.Value[..1000] : f.Value, inline = false }).ToArray(),
                    timestamp = DateTime.UtcNow.ToString("o"), footer = new { text = "BAMF scheduled report" } } },
            }) : "";
            var generic = JsonSerializer.Serialize(new { content = title + "\n" + text, message = text, title, fields = fields.Select(f => new { name = f.Name, value = f.Value }) });
            return BuildAlertRequest(url, format, title: title, message: text, priority: 3, tags: "clipboard",
                discordPayload: payload, genericPayload: generic);
        }
        try { return await Deliver("reports", title, text, Build, ct, holdable: false, only: only); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Report failed"); return false; }
    }

    /// <summary>A DHCP or DNS watch alert, to every destination that takes security alerts.</summary>
    private async Task SendWatchAlert(TrafficMonitor.Alert a, CancellationToken ct)
    {
        var text = $"BAMF: {a.Title}. {a.Detail}";
        HttpRequestMessage Build(string url, string format)
        {
            var payload = format == "discord" ? JsonSerializer.Serialize(new
            {
                username = "BAMF",
                embeds = new[] { new { title = "🚨 " + a.Title, description = a.Detail, color = 0xF2716F,
                    timestamp = DateTime.UtcNow.ToString("o"), footer = new { text = "BAMF network watch" } } },
            }) : "";
            var generic = JsonSerializer.Serialize(new { content = text, message = text, kind = a.Kind, title = a.Title, detail = a.Detail });
            return BuildAlertRequest(url, format, title: a.Title, message: a.Detail, priority: 5, tags: "rotating_light",
                discordPayload: payload, genericPayload: generic);
        }
        try { await Deliver("security", a.Title, text, Build, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Watch alert failed"); }
    }

    private static string FormatSpan(TimeSpan s)
    {
        if (s.TotalMinutes < 1) return "less than a minute";
        if (s.TotalHours < 1) return $"{(int)s.TotalMinutes} min";
        if (s.TotalDays < 1) return $"{(int)s.TotalHours} h {s.Minutes} min";
        return $"{(int)s.TotalDays} d {s.Hours} h";
    }

    // ---------------- notifications ----------------

    private async Task Notify(string mac, string ip, string hostname, string vendor, string subnet, CancellationToken ct)
        => await SendWebhook(mac, ip, hostname, vendor, subnet, test: false, ct);

    /// <summary>
    /// Sends a test notification to one destination ("main" for the main
    /// webhook, else an extra's id), whatever kinds it takes. Returns null on
    /// success, else an error description.
    /// </summary>
    public async Task<string?> SendTestNotification(CancellationToken ct, string destination = "main")
    {
        if (!Destinations().Any(d => d.Id == destination))
            return destination == "main" ? "No webhook URL saved. Add one under Tools → Notifications." : "That destination isn't saved.";
        try
        {
            var ok = await SendWebhook("AA:BB:CC:DD:EE:FF", "192.0.2.123", "test-device",
                "BAMF Test", SubnetLabels.FirstOrDefault() ?? "192.0.2.0/24", test: true, ct, destination);
            return ok ? null : _lastDeliveryError ?? "The webhook endpoint returned a non-success status. Check the URL.";
        }
        catch (Exception ex)
        {
            return $"Webhook call failed: {ex.Message}";
        }
    }

    private async Task<bool> SendWebhook(string mac, string ip, string hostname, string vendor, string subnet,
        bool test, CancellationToken ct, string? only = null)
    {
        var title = test ? "BAMF webhook test" : "New host detected";
        var text = $"BAMF: {(test ? "webhook test - " : "")}new host {mac} at {ip} on {subnet}" +
                   (hostname != "" ? $" ({hostname})" : "") +
                   (vendor != "" ? $" [{vendor}]" : "");

        HttpRequestMessage Build(string url, string format)
        {
            var payload = "";
            if (format == "discord")
            {
                // Rich Discord embed. Amber for real alerts, green for tests.
                payload = JsonSerializer.Serialize(new
                {
                    username = "BAMF",
                    embeds = new[]
                    {
                        new
                        {
                            title,
                            description = test
                                ? "If you can read this, notifications are wired up correctly."
                                : "An unknown device appeared on the network.",
                            color = test ? 0x3FDB7F : 0xFFB454,
                            fields = new object[]
                            {
                                new { name = "MAC",      value = $"`{mac}`", inline = true },
                                new { name = "IP",       value = $"`{ip}`",  inline = true },
                                new { name = "Network",  value = subnet,      inline = true },
                                new { name = "Hostname", value = hostname == "" ? "—" : hostname, inline = true },
                                new { name = "Vendor",   value = vendor == "" ? "—" : vendor,     inline = true },
                            },
                            timestamp = DateTime.UtcNow.ToString("o"),
                            footer = new { text = "Basic ARP Monitoring Framework" },
                        }
                    }
                });
            }
            // Generic JSON: "content" and "message" keep simple endpoints working.
            var generic = JsonSerializer.Serialize(new
            {
                content = text,
                message = text,
                mac, ip, hostname, vendor, subnet, test,
            });
            return BuildAlertRequest(url, format,
                title: title,
                message: text,
                priority: test ? 3 : 4,
                tags: test ? "test_tube" : "warning",
                discordPayload: payload, genericPayload: generic);
        }

        try { return await Deliver("devices", title, text, Build, ct, holdable: !test, only: only); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Webhook notification failed");
            if (test) throw;
            return false;
        }
    }
}
