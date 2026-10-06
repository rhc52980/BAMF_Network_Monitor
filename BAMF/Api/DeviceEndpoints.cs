using System.Text;
using System.Text.RegularExpressions;
using LanWatch.Services;
using static LanWatch.Api.ApiHelpers;

namespace LanWatch.Api;

/// <summary>Devices: the list, each one's details and history, scans and port scans.</summary>
internal static class DeviceEndpoints
{
    public static void Map(WebApplication app, string version, string buildDate)
    {

        app.MapGet("/api/hosts", (HttpContext ctx, HostStore store, ScannerService scanner, UpdateChecker updates, PortBlinker blinker, RemoteService remotesSvc, MeasuredTraffic measured) =>
        {
            var linkTemplate = app.Configuration["Bamf:DeviceLinkTemplate"];
            var placements = store.GetPlacements();
            var deviceTypes = store.GetDeviceTypes();
            var typeNames = store.GetTypeNames();
            var addresses = store.GetAddresses();
            var interfaces = store.GetInterfaces();
            var latency = store.LatestLatency();
            var uptimes = store.Uptimes();
            var tags = store.GetTags();
            // A device with extra network cards combined into it counts all of them.
            // What a switch or the router counts replaces what the capture saw.
            var fresh = measured.Fresh();
            var counters = TrafficMonitor.Combine(measured.Overlay(scanner.Traffic.Counters()), store.CardOwners()).ToDictionary(kv => kv.Key, kv => kv.Value.Counter, StringComparer.OrdinalIgnoreCase);
            var dnsByDevice = scanner.Traffic.DnsByDevice();
            var openPorts = store.OpenPorts();
            var routerNames = store.GetRouterNames();
            var ipv6 = store.GetIpv6(DateTime.UtcNow.AddDays(-7));
            var snoozes = store.GetSnoozes();
            var hosts = store.GetAll().Select(h => new
            {
                id = h.Id,
                hostname = string.IsNullOrEmpty(h.Hostname) ? "—" : h.Hostname,
                customName = h.CustomName,
                ip = h.Ip,
                mac = h.Mac,
                vendor = h.Vendor,
                subnet = h.Subnet,
                online = h.Online,
                known = h.Known,
                ignored = h.Ignored,
                watched = h.Watched,
                // When its alerts start going out again, while it's snoozed; null otherwise.
                snoozedUntil = snoozes.TryGetValue(h.Id, out var sz) ? sz : null,
                forgotten = h.Forgotten,
                note = h.Note,
                osGuess = h.OsGuess,
                mdnsName = h.MdnsName,
                routerName = routerNames.TryGetValue(h.Mac, out var rn) ? rn : null,
                // IPv6 addresses seen in the last week: global first, link-local last.
                ipv6 = ipv6.TryGetValue(h.Mac, out var v6) ? v6.Select(a => a.Ip).OrderBy(a => a.StartsWith("fe80", StringComparison.OrdinalIgnoreCase) ? 2 : a.StartsWith("fd", StringComparison.OrdinalIgnoreCase) || a.StartsWith("fc", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToList() : null,
                mdnsServices = h.MdnsServices,
                link = h.Link,                                              // raw override, for editing
                linkUrl = DeviceLink.Resolve(h.Link, h.Ip, linkTemplate),   // resolved, for the href
                firstSeen = h.FirstSeen,
                lastSeen = h.LastSeen,
                // Which switch port the user recorded this device on; 0 = not recorded.
                switchId = placements.TryGetValue(h.Id, out var pl) ? pl.SwitchId : 0,
                switchPort = placements.TryGetValue(h.Id, out var pp) ? pp.Port : 0,
                // The type the user set, overriding the guess for its icon and type chip; "" = BAMF's guess.
                deviceType = deviceTypes.TryGetValue(h.Id, out var dt) ? dt : "",
                // What the device is, in the user's own words; "" = BAMF's guess stands.
                typeName = typeNames.TryGetValue(h.Id, out var tn) ? tn : "",
                // Another network card of this device, combined into it; 0 = its own device.
                interfaceOf = interfaces.TryGetValue(h.Id, out var io) ? io : 0,
                // Round-trip time of the last echo, ms; null when it didn't answer or hasn't been probed.
                latencyMs = latency.TryGetValue(h.Id, out var lat) ? lat : null,
                // Share of the last 7 and 30 days online, percent, from the event history.
                uptime7 = uptimes.TryGetValue(h.Id, out var up) ? up.Week : null,
                uptime30 = uptimes.TryGetValue(h.Id, out var up2) ? up2.Month : null,
                tags = tags.TryGetValue(h.Id, out var tg) ? tg : new List<string>(),
                // Bytes per second in and out over the last ten seconds, and totals since the
                // traffic monitor started, as seen from this machine. Null when it isn't running.
                // Source is "switch" or "router" when one of them counted it, with where: "Office switch, port 5".
                traffic = counters.TryGetValue(h.Mac, out var tc) ? new { rx = Math.Round(tc.Rx), tx = Math.Round(tc.Tx), rxTotal = tc.RxTotal, txTotal = tc.TxTotal,
                    source = fresh.TryGetValue(h.Mac, out var fr) ? fr.Source : "capture", where = fr?.Where } : null,
                // The DNS servers this device asks, most used first.
                dns = dnsByDevice.TryGetValue(h.Mac, out var dl) ? dl : new List<string>(),
                // Ports found open the last time it was scanned, if ever.
                openPorts = openPorts.TryGetValue(h.Id, out var op) ? op : new List<int>(),
                // Every address the device answers on, the main one (ip) first. More
                // than one current entry means it's on several at once, like a router
                // with an address on each network. Old ones stay listed until they age out.
                addresses = (addresses.TryGetValue(h.Id, out var al) ? al : new List<HostAddress>()).Select(a => new
                {
                    ip = a.Ip,
                    subnet = a.Subnet,
                    firstSeen = a.FirstSeen,
                    lastSeen = a.LastSeen,
                    current = a.Current,
                    linkUrl = DeviceLink.Resolve(h.Link, a.Ip, linkTemplate),
                }),
            });
            return Results.Json(new
            {
                version,
                buildDate,
                subnets = scanner.SubnetLabels,
                scanModes = scanner.SubnetModes,
                activeArp = new { enabled = scanner.ActiveArpEnabled, npcapAvailable = scanner.NpcapAvailable },
                autoIgnoreRandom = scanner.AutoIgnoreRandomEnabled,
                latencyProbe = scanner.LatencyProbeEnabled,
                // The traffic monitor: bytes per device and DHCP/DNS watching, through Npcap.
                traffic = TrafficJson(scanner),
                // Other BAMF servers' devices, read-only, and how each remote is doing.
                // Who's looking: "admin", "viewer" (the view-only password), or "open" (no password set).
                role = ctx.Items["bamfRole"] as string ?? "open",
                remotes = remotesSvc.Hosts(),
                remoteStatus = remotesSvc.Statuses,
                // Holiday Spirit: the dashboard wears Halloween through October and
                // Christmas from December 1st to 25th. Saved setting wins over appsettings.
                holidaySpirit = HolidaySpirit(store, app.Configuration),
                night = NightJson(store),
                // Where the dashboard's feedback link points. Derived from the same
                // setting the update check uses, so a fork sends reports to its own
                // tracker rather than upstream's.
                repoUrl = $"https://github.com/{app.Configuration["Bamf:UpdateRepo"] ?? "rhc52980/BAMF_Network_Monitor"}",
                update = new
                {
                    enabled = updates.Enabled,
                    available = updates.UpdateAvailable,
                    latest = updates.LatestVersion,
                    url = updates.ReleaseUrl,
                    checkedUtc = updates.LastCheckedUtc?.ToString("o"),
                },
                webhookConfigured = !string.IsNullOrWhiteSpace(scanner.WebhookUrl),
                // True when any destination, the main webhook or another, takes any alerts.
                alertsConfigured = scanner.AnyDestination,
                // Whether "Don't remind me" was pressed on the alerts-off banner.
                alertsNudgeOff = store.GetSetting("alertsNudgeOff") == "true",
                // Whether Ping, Trace route and DNS lookup are offered in a device's menu.
                networkToolsEnabled = store.GetSetting("networkTools") != "false",
                // Whether the Tools menu offers a trace route to any address (off by default).
                traceAnywhereEnabled = store.GetSetting("traceAnywhere") == "true" && store.GetSetting("networkTools") != "false",
                // Whether the list's Select mode (one change to several devices) is offered.
                bulkEnabled = store.BulkEnabled,
                // The Pause alerts control: whether it's offered, and until when alerts are paused (null when they aren't).
                pauseEnabled = scanner.PauseEnabled,
                // Whether BAMF says so when it starts again after stopping unexpectedly.
                restartAlertEnabled = store.GetSetting("restartAlert") != "false",
                alertsPausedUntil = scanner.PausedUntil?.ToString("o"),
                newDays = NewDays(store),
                // Masked, never the full URL: anyone who can load the dashboard could
                // read it, and the token in a Discord webhook URL is the credential.
                webhookMasked = MaskWebhook(scanner.WebhookUrl),
                webhookFormat = scanner.WebhookFormat,
                lastScan = scanner.LastScanUtc?.ToString("o"),
                // The default interval, kept as-is so existing dashboard code and any
                // API consumer still reads what it always did.
                scanIntervalSeconds = scanner.ScanIntervalSeconds,
                // Effective interval per network, once per-network overrides are applied.
                subnetIntervalSeconds = scanner.SubnetIntervals,
                // When each network is next due, ISO-8601 UTC. Paused networks are absent.
                subnetNextDue = scanner.SubnetNextDue.ToDictionary(kv => kv.Key, kv => kv.Value.ToString("o")),
                // Per network: this machine's own address and MAC there, and the default
                // gateway on it. What the map marks as known rather than inferred.
                networkPlaces = scanner.NetworkPlaces(),
                // Gateways the user declared, one per network: [{ subnet, hostId, ip }].
                // Where one is declared it's the network's gateway, over the routing table.
                gateways = store.GetGateways().Select(g => new { subnet = g.Subnet, hostId = g.HostId, ip = g.Ip }),
                // The switch layout as the user described it. Not discovered: see HostStore.Switches.cs.
                switches = SwitchesJson(store),
                // A running "Find port", so every open dashboard pulses the device in
                // step with its switch light; serverTime lets a browser whose clock
                // differs line itself up. Bursts are on for [2k, 2k+1) s after started.
                blink = blinker.ActiveHostId is long bh && blinker.ActiveStartedUtc is DateTime bs && blinker.ActiveUntilUtc is DateTime bu
                    ? new { hostId = bh, started = bs.ToString("o"), until = bu.ToString("o") }
                    : null,
                serverTime = DateTime.UtcNow.ToString("o"),
                // Where the user dragged things on the topology Map: subnet -> node -> [x, y].
                mapPositions = store.GetMapPositions(),
                // Icons the user chose for whole guessed types: { "Linux": "server" }.
                typeIcons = store.GetTypeIcons(),
                hosts,
            });
        });

        // Plain-text device table. No JSON, no markup - meant for `curl`, a terminal,
        // or pointing a read-only agent at. Sorted by network then IP so diffs between
        // two fetches are meaningful.
        app.MapGet("/api/hosts.txt", (HostStore store, ScannerService scanner) =>
        {
            var hosts = store.GetAll()
                .OrderBy(h => h.Subnet, StringComparer.Ordinal)
                .ThenBy(h => IpSortKey(h.Ip))
                .ToList();
            var switchNames = store.GetSwitches().ToDictionary(s => s.Id, s => s.Name);
            var placements = store.GetPlacements();
            var portLabels = store.GetPortLabels();
            var typeNames = store.GetTypeNames();
            var addresses = store.GetAddresses();
            string IpCell(HostRecord h)
            {
                var also = addresses.TryGetValue(h.Id, out var al) ? al.Count(a => a.Current && a.Ip != h.Ip) : 0;
                return also > 0 ? $"{h.Ip} (+{also})" : h.Ip;
            }
            string PluggedInto(long id)
            {
                if (!placements.TryGetValue(id, out var p) || !switchNames.TryGetValue(p.SwitchId, out var sw)) return "-";
                if (p.Port <= 0) return sw;
                var where = portLabels.TryGetValue(p.SwitchId, out var l) && l.TryGetValue(p.Port, out var t) ? $" ({t})" : "";
                return $"{sw} port {p.Port}{where}";
            }

            var rows = hosts.Select(h => new[]
            {
                h.CustomName != "" ? h.CustomName : (h.Hostname != "" ? h.Hostname : "-"),
                IpCell(h),
                h.Mac,
                h.Vendor == "" ? "-" : h.Vendor,
                h.Subnet == "" ? "-" : h.Subnet,
                h.Online ? "online" : "offline",
                string.Concat(h.Known ? "K" : "-", h.Ignored ? "I" : "-", h.Watched ? "W" : "-", h.Forgotten ? "F" : "-"),
                typeNames.TryGetValue(h.Id, out var tn) ? $"{tn} (your type)" : h.OsGuess == "" ? "-" : h.OsGuess,
                h.LastSeen,
                PluggedInto(h.Id),
                h.Note == "" ? "-" : h.Note,
            }).ToList();

            string[] headers = { "NAME", "IP", "MAC", "VENDOR", "NETWORK", "STATUS", "FLAGS", "DEVICE GUESS", "LAST SEEN", "PLUGGED INTO", "NOTE" };
            var widths = headers.Select((hd, i) =>
                Math.Max(hd.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();

            var sb = new StringBuilder();
            sb.AppendLine($"BAMF {version} - {hosts.Count} device(s) - generated {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"networks: {(scanner.SubnetLabels.Count == 0 ? "-" : string.Join(", ", scanner.SubnetLabels))}");
            sb.AppendLine($"last scan: {(scanner.LastScanUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "never")} UTC" +
                          $"   flags: K=known I=ignored W=watched F=forgotten");
            sb.AppendLine();
            sb.AppendLine(string.Join("  ", headers.Select((hd, i) => hd.PadRight(widths[i]))).TrimEnd());
            sb.AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
            foreach (var r in rows)
                sb.AppendLine(string.Join("  ", r.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());

            return Results.Text(sb.ToString(), "text/plain; charset=utf-8");
        });

        // Deeper device identification, on demand only: one ICMP echo for the TTL plus
        // a short fingerprint-port probe. Never runs on its own.
        app.MapPost("/api/hosts/{id:long}/identify", async (long id, HostStore store, CancellationToken ct) =>
        {
            var host = store.GetAll().FirstOrDefault(h => h.Id == id);
            if (host is null) return Results.NotFound();

            int? ttl = null;
            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                var reply = await ping.SendPingAsync(host.Ip, 700);
                if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                    ttl = reply.Options?.Ttl;
            }
            catch { /* no reply is itself a (weak) signal; fall through */ }

            // Ports chosen for what they reveal about the device, not for coverage.
            int[] fingerprintPorts = { 22, 80, 139, 443, 445, 631, 3389, 5900, 9100, 32400, 62078 };
            var open = await PortChecker.ScanAsync(host.Ip, fingerprintPorts, 700, ct);
            var openPorts = open.Select(o => o.Port).ToList();

            var guess = OsFingerprint.Active(ttl, host.Vendor, host.Hostname, openPorts);
            store.SetOsGuess(id, guess);

            return Results.Json(new
            {
                ok = true,
                osGuess = guess,
                ttl,
                openPorts,
                reachable = ttl is not null,
            });
        });

        app.MapPost("/api/hosts/{id:long}/wake", async (long id, HostStore store) =>
        {
            var host = store.GetAll().FirstOrDefault(h => h.Id == id);
            if (host is null) return Results.NotFound();

            System.Net.IPAddress? directed = null;
            if (!string.IsNullOrEmpty(host.Subnet) && host.Subnet.Contains('/'))
            {
                var parts = host.Subnet.Split('/');
                if (System.Net.IPAddress.TryParse(parts[0], out var net) && int.TryParse(parts[1], out var prefix))
                    directed = WakeOnLan.DirectedBroadcast(net, prefix);
            }

            var ok = await WakeOnLan.WakeAsync(host.Mac, directed);
            return ok ? Results.Ok(new { ok = true }) : Results.Json(new { ok = false, error = "Could not send magic packet (bad MAC?)" });
        });

        app.MapGet("/api/hosts/{id:long}/portscan", async (long id, string? ports, HostStore store, RuleService rules, CancellationToken ct) =>
        {
            var host = store.GetAll().FirstOrDefault(h => h.Id == id);
            if (host is null) return Results.NotFound();
            var spec = PortChecker.ParseSpec(ports);
            var open = spec is null
                ? await PortChecker.ScanAsync(host.Ip, 700, ct)
                : await PortChecker.ScanAsync(host.Ip, spec, 700, ct);
            // Remembered, so a port that opens later is a change worth an alert.
            var newly = await rules.RecordScan(host, spec ?? PortChecker.CommonPorts.Select(p => p.Port), open, ct);
            return Results.Json(new { ports = open.Select(o => new { port = o.Port, service = o.Service }), newlyOpen = newly });
        });

        // Every port BAMF has found open on a device, open now or once.
        app.MapGet("/api/hosts/{id:long}/ports", (long id, HostStore store) =>
            Results.Json(store.GetPorts(id).Select(p => new { p.Port, p.Service, p.FirstSeen, p.LastSeen, p.Open })));

        // Bytes per hour for a device over the last days, from the traffic monitor.
        app.MapGet("/api/hosts/{id:long}/traffic", (long id, int? days, HostStore store) =>
        {
            var host = store.GetAll().FirstOrDefault(h => h.Id == id);
            if (host is null) return Results.NotFound();
            var macs = new List<string> { host.Mac };
            macs.AddRange(store.GetInterfaces().Where(kv => kv.Value == id).Select(kv => store.GetAll().FirstOrDefault(h => h.Id == kv.Key)?.Mac).Where(m => m is not null)!);
            var rows = macs.SelectMany(m => store.GetTrafficHistory(m, days ?? 7)).GroupBy(r => r.Hour).OrderBy(g => g.Key)
                .Select(g => new { hour = g.Key, rx = g.Sum(r => r.Rx), tx = g.Sum(r => r.Tx) });
            return Results.Json(rows);
        });

        // Runs the port watch now.
        // A sweep now, from the dashboard's Scan menu: every network, or one. The
        // inbound hook below does the same for scripts, behind the hook token.
        app.MapPost("/api/scan", (string? subnet, ScannerService scanner) =>
        {
            if (!string.IsNullOrWhiteSpace(subnet) && !scanner.SubnetLabels.Contains(subnet.Trim(), StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"{subnet} isn't a configured network." });
            scanner.RequestScan(subnet);
            return Results.Json(new { ok = true, networks = string.IsNullOrWhiteSpace(subnet) ? scanner.SubnetLabels : new[] { subnet.Trim() } });
        });

        app.MapPost("/api/ports/watch", async (RuleService rules, CancellationToken ct) =>
            Results.Json(new { newlyOpen = await rules.PortWatch(ct) }));

        // On-demand scan of any IP the user types — it doesn't have to be a known
        // host. Restricted to private/loopback ranges: the dashboard can be exposed
        // with no password, and this shouldn't become an internet port scanner.
        app.MapGet("/api/portscan/ip", async (string? ip, string? ports, HostStore store, CancellationToken ct) =>
        {
            if (!System.Net.IPAddress.TryParse(ip, out var addr) ||
                addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return Results.BadRequest(new { error = "Enter a valid IPv4 address." });

            if (!IsPrivateAddress(addr))
                return Results.BadRequest(new { error = "Only private network addresses can be scanned." });

            var target = addr.ToString();
            var spec = PortChecker.ParseSpec(ports);
            var open = spec is null
                ? await PortChecker.ScanAsync(target, 700, ct)
                : await PortChecker.ScanAsync(target, spec, 700, ct);

            // If we happen to know this address, label the result with its name.
            var host = store.GetAll().FirstOrDefault(h => h.Ip == target);
            return Results.Json(new
            {
                ip = target,
                name = host is null
                    ? ""
                    : (host.CustomName != "" ? host.CustomName : (host.Hostname != "" ? host.Hostname : host.Mac)),
                ports = open.Select(o => new { port = o.Port, service = o.Service }),
            });
        });

        // Wildcard scan: "*.245" hits that last octet on every configured network,
        // "192.168.2.*" walks a subnet. Expansion is limited to the networks BAMF is
        // configured for, so a pattern can't reach somewhere it isn't already looking.
        app.MapGet("/api/portscan/pattern", async (string? ip, string? ports, HostStore store, ScannerService scanner, CancellationToken ct) =>
        {
            const int maxTargets = 256;
            const int maxSubnetSize = 65536;

            if (string.IsNullOrWhiteSpace(ip) || !(ip.Contains('*') || ip.Contains('?')))
                return Results.BadRequest(new { error = "Pattern must contain * or ?, e.g. *.245" });

            var rx = new Regex("^" + string.Concat(ip.Select(c =>
                c == '*' ? ".*" : c == '?' ? "." : Regex.Escape(c.ToString()))) + "$");

            var targets = new List<(string Ip, string Subnet)>();
            // A paused network is off-limits to a wildcard as well: pausing means
            // "leave it alone", and a pattern reaching into it would be the one way
            // BAMF still put packets there. Read the saved list rather than the
            // scanner's cached copy so a pause takes effect here the moment it is
            // saved, not up to a minute later when the loop next wakes.
            var pausedNow = scanner.ReadDisabledSubnets();
            foreach (var label in scanner.SubnetLabels.Where(l => !pausedNow.Contains(l)))
            {
                var parts = label.Split('/');
                if (parts.Length != 2 ||
                    !System.Net.IPAddress.TryParse(parts[0], out var net) ||
                    !int.TryParse(parts[1], out var prefix)) continue;

                var size = prefix >= 31 ? 2L : 1L << (32 - prefix);
                if (size > maxSubnetSize) continue;   // too wide to enumerate sanely

                var baseKey = IpSortKey(net.ToString());
                for (var i = 1; i < size - 1; i++)
                {
                    var k = baseKey + i;
                    var addr = $"{(k >> 24) & 255}.{(k >> 16) & 255}.{(k >> 8) & 255}.{k & 255}";
                    if (!rx.IsMatch(addr)) continue;
                    targets.Add((addr, label));
                    if (targets.Count > maxTargets) break;
                }
                if (targets.Count > maxTargets) break;
            }

            if (targets.Count == 0)
            {
                // Name only the networks a pattern may actually reach. Listing a paused
                // one here would claim the pattern matched nothing on it, when in fact
                // it matched and was excluded because the network is switched off.
                var active = scanner.SubnetLabels.Where(l => !pausedNow.Contains(l)).ToList();
                return Results.BadRequest(new { error = $"'{ip}' matches no address on {(active.Count == 0 ? "any active network" : string.Join(", ", active))}." });
            }
            if (targets.Count > maxTargets)
                return Results.BadRequest(new { error = $"'{ip}' expands past {maxTargets} addresses. Narrow it." });

            var spec = PortChecker.ParseSpec(ports);
            var known = store.GetAll().ToDictionary(h => h.Ip, h => h, StringComparer.OrdinalIgnoreCase);
            var results = new List<object>();
            // Hosts run a few at a time, and each gets a slice of the single-host
            // budget rather than the whole thing, so the two limits can't multiply
            // into a thousand half-open sockets aimed through the router.
            const int hostConcurrency = 8;
            var perHost = PortChecker.PerHostBudget(hostConcurrency);
            using var sem = new SemaphoreSlim(hostConcurrency);
            await Task.WhenAll(targets.Select(async t =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var open = spec is null
                        ? await PortChecker.ScanAsync(t.Ip, 700, ct, perHost)
                        : await PortChecker.ScanAsync(t.Ip, spec, 700, ct, perHost);
                    if (open.Count == 0) return;
                    known.TryGetValue(t.Ip, out var h);
                    lock (results)
                        results.Add(new
                        {
                            id = h?.Id ?? 0,
                            name = h is null ? t.Ip : (h.CustomName != "" ? h.CustomName : (h.Hostname != "" ? h.Hostname : h.Mac)),
                            ip = t.Ip,
                            subnet = t.Subnet,
                            ports = open.Select(o => new { port = o.Port, service = o.Service }),
                        });
                }
                finally { sem.Release(); }
            }));

            return Results.Json(new { scanned = targets.Count, withOpenPorts = results.Count, hosts = results });
        });

        // Network-wide, on-demand scan. Optional ?ports=... custom spec, optional
        // ?subnet=... to limit to one network. Online hosts only (offline can't answer).
        app.MapGet("/api/portscan", async (string? ports, string? subnet, HostStore store, RuleService rules, CancellationToken ct) =>
        {
            var spec = PortChecker.ParseSpec(ports);
            var hosts = store.GetAll()
                .Where(h => h.Online && !h.Ignored)
                .Where(h => string.IsNullOrEmpty(subnet) || h.Subnet == subnet)
                .ToList();

            var results = new List<object>();
            // Scan hosts a few at a time so we don't blast the whole subnet at once,
            // and split the single-host budget between them so the two limits can't
            // multiply into a thousand concurrent connects.
            const int hostConcurrency = 8;
            var perHost = PortChecker.PerHostBudget(hostConcurrency);
            using var sem = new SemaphoreSlim(hostConcurrency);
            var tasks = hosts.Select(async h =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var open = spec is null
                        ? await PortChecker.ScanAsync(h.Ip, 700, ct, perHost)
                        : await PortChecker.ScanAsync(h.Ip, spec, 700, ct, perHost);
                    await rules.RecordScan(h, spec ?? PortChecker.CommonPorts.Select(p => p.Port), open, ct);
                    if (open.Count > 0)
                        lock (results)
                            results.Add(new
                            {
                                id = h.Id,
                                name = h.CustomName != "" ? h.CustomName : (h.Hostname != "" ? h.Hostname : h.Mac),
                                ip = h.Ip,
                                subnet = h.Subnet,
                                ports = open.Select(o => new { port = o.Port, service = o.Service }),
                            });
                }
                finally { sem.Release(); }
            });
            await Task.WhenAll(tasks);
            return Results.Json(new { scanned = hosts.Count, withOpenPorts = results.Count, hosts = results });
        });

        app.MapGet("/api/events", (HostStore store) =>
            Results.Json(store.GetRecentEvents(250).Select(e => new
            {
                type = e.Type, at = e.At, hostId = e.HostId, mac = e.Mac, ip = e.Ip,
                name = e.CustomName != "" ? e.CustomName : (e.Hostname != "" ? e.Hostname : e.Mac),
                subnet = e.Subnet,
            })));

        // Latency samples for one device: [{ at, ms }], ms null for no reply. ?hours=24 by default.
        app.MapGet("/api/hosts/{id:long}/latency", (long id, int? hours, HostStore store) =>
            Results.Json(store.GetLatency(id, hours ?? 24).Select(s => new { at = s.At, ms = s.Ms })));

        // Replaces a device's tags: { "tags": ["kids", "IoT"] }.
        app.MapPost("/api/hosts/{id:long}/tags", (long id, TagsRequest body, HostStore store) =>
        {
            var error = store.SetTags(id, body.Tags ?? new());
            return error is null ? Results.Ok() : Results.BadRequest(new { error });
        });

        // Everything the Activity tab's traffic cards show: top talkers with their
        // five-minute strips, the DHCP and DNS servers seen, and the watch alerts.
        app.MapGet("/api/traffic", (HostStore store, ScannerService scanner, MeasuredTraffic measured, SwitchCounters switchCounters, RouterImport import) =>
        {
            var t = scanner.Traffic;
            var hosts = store.GetAll().Where(h => !h.Forgotten).ToDictionary(h => h.Mac, h => h, StringComparer.OrdinalIgnoreCase);
            string Name(string mac) => hosts.TryGetValue(mac, out var h) ? (h.CustomName != "" ? h.CustomName : h.Hostname != "" ? h.Hostname : h.Ip) : mac;
            // One line per device: a machine with several network cards combined into
            // one device is counted once, with all its cards' traffic added up.
            var fresh = measured.Fresh();
            var top = TrafficMonitor.Combine(measured.Overlay(t.Counters()), store.CardOwners())
                .Where(kv => hosts.ContainsKey(kv.Key) || kv.Value.Counter.RxTotal + kv.Value.Counter.TxTotal > 0)
                .OrderByDescending(kv => kv.Value.Counter.RxTotal + kv.Value.Counter.TxTotal)
                .Take(12)
                .Select(kv => new
                {
                    mac = kv.Key, hostId = hosts.TryGetValue(kv.Key, out var h) ? h.Id : 0, name = Name(kv.Key),
                    ip = hosts.TryGetValue(kv.Key, out var h2) ? h2.Ip : "",
                    cards = kv.Value.Cards,
                    source = fresh.TryGetValue(kv.Key, out var fr) ? fr.Source : "capture", where = fr?.Where,
                    rxTotal = kv.Value.Counter.RxTotal, txTotal = kv.Value.Counter.TxTotal, rx = Math.Round(kv.Value.Counter.Rx), tx = Math.Round(kv.Value.Counter.Tx), strip = kv.Value.Counter.Strip,
                });
            // What else is counting, for the card's subtitle.
            var sources = switchCounters.Statuses().Where(s => s.Enabled)
                .Select(s => new { kind = "switch", name = s.Name, ok = s.Error is null && s.LastPoll is not null, error = s.Error, devices = s.Counted })
                .ToList();
            var ri = import.Current;
            if (ri.Traffic) sources.Add(new { kind = "router", name = "UniFi", ok = ri.TrafficError is null && ri.TrafficAt is not null, error = ri.TrafficError, devices = ri.TrafficClients });
            return Results.Json(new
            {
                status = TrafficJson(scanner),
                sources,
                top,
                dhcp = t.DhcpServers().Select(s => new { s.Ip, s.Mac, name = Name(s.Mac), s.LastSeen, s.Offers, trusted = t.KnownDhcp.Contains(s.Ip) }),
                dns = t.DnsServers().Select(s => new { s.Ip, s.LastSeen, s.Clients, s.Queries, trusted = t.KnownDns.Contains(s.Ip),
                    name = hosts.Values.FirstOrDefault(h => h.Ip == s.Ip) is { } dh ? (dh.CustomName != "" ? dh.CustomName : dh.Hostname) : "" }),
                alerts = t.Alerts(),
            });
        });

        // The IPv6 watch: its state, and MACs seen only over IPv6.
        app.MapGet("/api/ipv6", (HostStore store, ScannerService scanner, OuiLookup oui) =>
        {
            var known = store.GetAll().Select(h => h.Mac).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var only = store.GetIpv6(DateTime.UtcNow.AddDays(-7)).Where(kv => !known.Contains(kv.Key))
                .Select(kv => new { mac = kv.Key, vendor = oui.Lookup(kv.Key), addresses = kv.Value.Select(a => a.Ip), lastSeen = kv.Value.Max(a => a.LastSeen) })
                .OrderByDescending(x => x.lastSeen).ToList();
            return Results.Json(new { enabled = scanner.Ipv6WatchEnabled, lastRead = scanner.LastIpv6Read?.ToString("o"), error = scanner.Ipv6Error, onlyIpv6 = only });
        });

        // When a device is usually online: a week of hours, over the last few weeks.
        app.MapGet("/api/hosts/{id:long}/presence", (long id, int? weeks, HostStore store) =>
        {
            var w = Math.Clamp(weeks ?? 4, 1, 12);
            return Results.Json(new { weeks = w, grid = store.Presence(id, w) });
        });

        // What changed over a period: arrivals, departures, moves, ports and alerts.
        app.MapGet("/api/changes", (int? days, HostStore store, ScannerService scanner) =>
        {
            var d = Math.Clamp(days ?? 7, 1, 90);
            var since = DateTime.UtcNow.AddDays(-d);
            var c = store.ChangesSince(since);
            var hosts = store.GetAll().ToDictionary(h => h.Id);
            var routerNames = store.GetRouterNames();
            string Name(long id) => hosts.TryGetValue(id, out var h)
                ? h.CustomName != "" ? h.CustomName : h.Hostname != "" ? h.Hostname : routerNames.TryGetValue(h.Mac, out var rn) && rn != "" ? rn : h.Ip
                : "";
            string Ip(long id) => hosts.TryGetValue(id, out var h) ? h.Ip : "";
            var counts = new Dictionary<string, int>(c.AlertCounts);
            foreach (var a in scanner.Traffic.Alerts().Where(a => DateTime.TryParse(a.At, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) && t >= since))
                counts[a.Kind] = counts.GetValueOrDefault(a.Kind) + 1;
            object Dev(HostStore.ChangeDevice x) => new { hostId = x.Id, name = Name(x.Id), ip = Ip(x.Id), at = x.At, detail = x.Detail };
            object Port(HostStore.ChangePort x) => new { hostId = x.HostId, name = Name(x.HostId), ip = Ip(x.HostId), port = x.Port, service = x.Service, at = x.At };
            return Results.Json(new
            {
                days = d, since = since.ToString("o"),
                arrived = c.Arrived.Select(Dev), left = c.Left.Select(Dev), moved = c.Moved.Select(Dev),
                opened = c.Opened.Select(Port), closed = c.Closed.Select(Port),
                alerts = counts, notable = c.Notable.Select(a => new { at = a.At, kind = a.Kind, title = a.Title }),
            });
        });

        // Names from the router, and the free addresses on each network.
        app.MapGet("/api/router-import", (RouterImport import) => Results.Json(import.Current));

        app.MapPost("/api/router-import/run", async (RouterImport import, CancellationToken ct) =>
        {
            var n = await import.Run(ct);
            return n < 0 ? Results.Json(import.Current, statusCode: 502) : Results.Json(import.Current);
        });

        app.MapPost("/api/router-import/apply", (RouterNamesApply body, HostStore store) =>
            Results.Json(new { named = store.ApplyRouterNames(body.Overwrite) }));

        app.MapGet("/api/free-ips", (int? days, HostStore store, ScannerService scanner) =>
        {
            var window = Math.Clamp(days ?? 90, 1, 3650);
            var since = DateTime.UtcNow.AddDays(-window);
            var places = scanner.NetworkPlaces();
            var list = new List<object>();
            foreach (var label in scanner.SubnetLabels)
            {
                var parts = label.Split('/');
                if (parts.Length != 2 || !System.Net.IPAddress.TryParse(parts[0], out var net) || net.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                    || !int.TryParse(parts[1], out var prefix) || prefix < 16 || prefix > 30) continue;
                var used = store.UsedAddresses(label, since);
                if (places.TryGetValue(label, out var place))
                {
                    if (place.Gateway is { } g) used.Add(g);
                    if (place.SelfIp is { } me) used.Add(me);
                }
                foreach (var gw in store.GetGateways().Where(g => g.Subnet == label)) used.Add(gw.Ip);
                var bytes = net.GetAddressBytes();
                var start = ((uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3]) & (uint.MaxValue << (32 - prefix));
                var size = (int)((1u << (32 - prefix)) - 2);
                string Ip(uint v) => $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";
                var runs = new List<(uint From, uint To)>();
                uint? runStart = null;
                var free = 0;
                for (uint v = start + 1; v <= start + (uint)size; v++)
                {
                    var isFree = !used.Contains(Ip(v));
                    if (isFree) { free++; runStart ??= v; }
                    if ((!isFree || v == start + (uint)size) && runStart is { } rs) { runs.Add((rs, isFree ? v : v - 1)); runStart = null; }
                }
                var longest = runs.OrderByDescending(r => r.To - r.From).FirstOrDefault();
                list.Add(new
                {
                    subnet = label, size, used = size - free, free, days = window,
                    suggestion = runs.Count > 0 ? Ip(longest.From) : null,
                    runs = runs.OrderByDescending(r => r.To - r.From).ThenBy(r => r.From).Take(8)
                        .Select(r => new { from = Ip(r.From), to = Ip(r.To), count = (int)(r.To - r.From + 1) }),
                });
            }
            return Results.Json(list);
        });

        app.MapGet("/api/hosts/{id:long}/events", (long id, HostStore store) =>
            Results.Json(store.GetEvents(id).Select(e => new { type = e.Type, at = e.At })));

        // Every address the host has been seen at, oldest first. Each entry runs from
        // its own timestamp until the next entry's; the last one is the current
        // address and has no end.
        app.MapGet("/api/hosts/{id:long}/ips", (long id, HostStore store) =>
        {
            var rows = store.GetIpHistory(id);
            return Results.Json(rows.Select((r, i) => new
            {
                ip = r.Ip,
                from = r.At,
                to = i + 1 < rows.Count ? rows[i + 1].At : null,
            }));
        });

        // One change to several devices at once, from the list's Select mode.
        app.MapPost("/api/hosts/bulk", (BulkRequest body, HostStore store) =>
        {
            if (!store.BulkEnabled) return Results.Conflict(new { error = "Selecting several devices is switched off in Settings." });
            var (result, error) = store.Bulk(body.Ids, body.Action, body.Tag, body.Minutes);
            return result is null ? Results.BadRequest(new { error }) : Results.Json(new { done = result.Done, missing = result.Missing, failed = result.Failed });
        });

        // Undo for a bulk change: each device put back to how it stood before.
        app.MapPost("/api/hosts/bulk/restore", (BulkRestoreRequest body, HostStore store) =>
        {
            if (!store.BulkEnabled) return Results.Conflict(new { error = "Selecting several devices is switched off in Settings." });
            var (result, error) = store.RestoreStates(body.States?.Select(s => new HostStore.HostState(s.Id, s.Known, s.Watched, s.Ignored, s.Forgotten, s.Tags, s.SnoozedUntil)));
            return result is null ? Results.BadRequest(new { error }) : Results.Json(new { done = result.Done, missing = result.Missing, failed = result.Failed });
        });

        // Whether Select mode is offered at all.
        app.MapPost("/api/settings/bulk", (ActiveArpRequest body, HostStore store) =>
        {
            store.SetSetting("bulkSelect", body.Enabled ? "true" : "false");
            return Results.Json(new { enabled = store.BulkEnabled });
        });

        // Ping, trace route or DNS lookup for a device, run from this machine. The target is the device's own address.
        app.MapPost("/api/hosts/{id:long}/tool", async (long id, ToolRequest body, HostStore store, NetworkTools tools, CancellationToken ct) =>
        {
            if (store.GetSetting("networkTools") == "false") return Results.Conflict(new { error = "Network tools are switched off in Settings." });
            var tool = (body.Tool ?? "").Trim().ToLowerInvariant();
            if (!NetworkTools.Tools.Contains(tool)) return Results.BadRequest(new { error = "Ping, trace or dns." });
            var h = store.GetAll().FirstOrDefault(x => x.Id == id && !x.Forgotten);
            if (h is null) return Results.NotFound();
            if (!System.Net.IPAddress.TryParse(h.Ip, out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return Results.BadRequest(new { error = "BAMF doesn't have an IPv4 address for that device." });
            var name = !string.IsNullOrEmpty(h.Hostname) && h.Hostname != "—" ? h.Hostname : h.MdnsName;
            var r = await tools.RunAsync(tool, h.Ip, name, ct);
            return r is null ? Results.Json(new { error = "Another tool is running. Try again in a moment." }, statusCode: 429)
                : Results.Json(new { tool = r.Tool, target = r.Target, ok = r.Ok, lines = r.Lines, summary = r.Summary });
        });

        // A ping, trace route or path ping to an address or name that was typed, anywhere including the internet. Off until switched on, and the main password
        // only (a view-only password can't POST at all). The target is checked, a name is looked up here, and nothing typed reaches a command.
        app.MapPost("/api/tools/anywhere", async (TraceTargetRequest body, HostStore store, NetworkTools tools, CancellationToken ct) =>
        {
            if (store.GetSetting("networkTools") == "false") return Results.Conflict(new { error = "Network tools are switched off in Settings." });
            if (store.GetSetting("traceAnywhere") != "true") return Results.Conflict(new { error = "Tracing to any address is switched off in Settings." });
            try
            {
                var r = await tools.RunAnywhere((body.Tool ?? "trace").Trim().ToLowerInvariant(), body.Target, ct);
                return r is null ? Results.Json(new { error = "Another tool is running. Try again in a moment." }, statusCode: 429)
                    : Results.Json(new { tool = r.Tool, target = r.Target, ok = r.Ok, lines = r.Lines, summary = r.Summary });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 429); }
        });

        // Whether tracing to an address that was typed is offered. Off by default.
        app.MapPost("/api/settings/trace-anywhere", (ActiveArpRequest body, HostStore store) =>
        {
            store.SetSetting("traceAnywhere", body.Enabled ? "true" : "false");
            return Results.Json(new { enabled = body.Enabled });
        });

        // Whether the network tools are offered at all.
        app.MapPost("/api/settings/network-tools", (ActiveArpRequest body, HostStore store) =>
        {
            store.SetSetting("networkTools", body.Enabled ? "true" : "false");
            return Results.Json(new { enabled = body.Enabled });
        });
        // Saved views of the device list: a name for a tab, network, status, type, tag and search.
        app.MapGet("/api/views", (HostStore store) => Results.Json(new { max = HostStore.MaxViews, views = store.GetViews() }));

        app.MapPost("/api/views", (ViewRequest body, HostStore store) =>
        {
            var error = store.SaveView(new HostStore.SavedView(body.Name ?? "", body.Tab ?? "", body.Network ?? "", body.Status ?? "", body.Guess ?? "", body.Tag ?? "", body.Query ?? ""));
            return error is null ? Results.Json(new { max = HostStore.MaxViews, views = store.GetViews() }) : Results.BadRequest(new { error });
        });

        app.MapPost("/api/views/delete", (ViewDeleteRequest body, HostStore store) =>
            store.DeleteView(body.Name) ? Results.Json(new { max = HostStore.MaxViews, views = store.GetViews() }) : Results.NotFound(new { error = "There's no view by that name." }));

        app.MapPost("/api/hosts/{id:long}/known", (long id, KnownRequest body, HostStore store) =>
            store.SetKnown(id, body.Known) ? Results.Ok() : Results.NotFound());

        app.MapPost("/api/hosts/{id:long}/watch", (long id, WatchRequest body, HostStore store) =>
            store.SetWatched(id, body.Watched) ? Results.Ok() : Results.NotFound());

        // Snooze a device's alerts for a while; 0 minutes ends the snooze.
        app.MapPost("/api/hosts/{id:long}/snooze", (long id, SnoozeRequest body, HostStore store) =>
        {
            var h = store.GetAll().FirstOrDefault(x => x.Id == id);
            if (h is null) return Results.NotFound();
            if (body.Minutes <= 0) { store.Unsnooze(id); return Results.Json(new { snoozedUntil = (string?)null }); }
            if (body.Minutes > HostStore.MaxSnoozeMinutes) return Results.BadRequest(new { error = "A week at most." });
            var until = DateTime.UtcNow.AddMinutes(body.Minutes);
            store.SnoozeHost(id, until, h.Online);
            return Results.Json(new { snoozedUntil = store.GetSnoozes().GetValueOrDefault(id) });
        });

        app.MapPost("/api/hosts/{id:long}/ignore", (long id, IgnoreRequest body, HostStore store) =>
            store.SetIgnored(id, body.Ignored) ? Results.Ok() : Results.NotFound());

        app.MapPost("/api/hosts/{id:long}/note", (long id, NoteRequest body, HostStore store) =>
        {
            var note = (body.Note ?? "").Trim();
            if (note.Length > 500) note = note[..500];
            return store.SetNote(id, note) ? Results.Ok() : Results.NotFound();
        });

        // Per-host link override. Accepts a bare port ("8006"), ":8006/admin", or a
        // full URL with an optional {ip} placeholder. Empty clears it back to the
        // global template. The resolved URL is returned so the caller can see what it
        // will actually open - including when the input was rejected as non-http(s).
        app.MapPost("/api/hosts/{id:long}/link", (long id, LinkRequest body, HostStore store) =>
        {
            var host = store.GetAll().FirstOrDefault(h => h.Id == id);
            if (host is null) return Results.NotFound();

            var raw = (body.Link ?? "").Trim();
            if (raw.Length > 200) raw = raw[..200];
            if (!store.SetLink(id, raw)) return Results.NotFound();

            return Results.Json(new
            {
                ok = true,
                link = raw,
                linkUrl = DeviceLink.Resolve(raw, host.Ip, app.Configuration["Bamf:DeviceLinkTemplate"]),
            });
        });

        app.MapPost("/api/hosts/{id:long}/name", (long id, NameRequest body, HostStore store) =>
        {
            var name = (body.Name ?? "").Trim();
            if (name.Length > 60) name = name[..60];
            return store.SetName(id, name) ? Results.Ok() : Results.NotFound();
        });

        // "Forget" is now a soft-delete (reversible from the Forgotten tab).
        app.MapPost("/api/hosts/{id:long}/forget", (long id, ForgetRequest body, HostStore store) =>
            store.SetForgotten(id, body.Forgotten) ? Results.Ok() : Results.NotFound());

        // Permanent delete (from the Forgotten tab).
        app.MapDelete("/api/hosts/{id:long}", (long id, HostStore store) =>
            store.DeletePermanent(id) ? Results.Ok() : Results.NotFound());

        // A device's type, overriding BAMF's guess for its icon and type chip. Empty
        // goes back to the guess.
        // The device type, in the user's words: shown in the list, grouped by the type chips.
        app.MapPost("/api/hosts/{id:long}/typename", (long id, DeviceTypeRequest body, HostStore store) =>
        {
            var error = store.SetTypeName(id, body.Type);
            return error is null ? Results.Ok() : Results.BadRequest(new { error });
        });

        // The Map icon (named "type" from when it was both).
        app.MapPost("/api/hosts/{id:long}/type", (long id, DeviceTypeRequest body, HostStore store) =>
        {
            var error = store.SetDeviceType(id, body.Type);
            return error is null ? Results.Ok() : Results.BadRequest(new { error });
        });

        // One device's story, newest first.
        app.MapGet("/api/hosts/{id:long}/timeline", (long id, HostStore store) =>
            Results.Json(store.Timeline(id).Select(t => new { at = t.At, kind = t.Kind, text = t.Text })));
    }
}
