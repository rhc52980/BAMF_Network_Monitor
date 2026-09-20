using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LanWatch.Services;

// Content root is pinned to the exe's own folder, and it has to be done here
// rather than through builder.Host afterwards. BAMF keeps appsettings.json,
// bamf.db and wwwroot beside the binary, so the content root must be the exe's
// directory no matter where it was launched from - a Windows service starts in
// System32, and a person double-clicking or running it from another folder
// starts wherever they were.
//
// Setting it after the builder exists throws NotSupportedException the moment
// the value differs from the launch directory, which is exactly the case this
// is here to handle: it worked as a service (both already C:\BAMF) and crashed
// for anyone running the binary from anywhere else, with a message that points
// at host configuration rather than at the real problem.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// As a Home Assistant add-on, the options from the add-on's Configuration tab
// arrive as /data/options.json; they go on top of appsettings.json.
if (File.Exists(HomeAssistantAddon.OptionsPath))
    builder.Configuration.AddInMemoryCollection(HomeAssistantAddon.Read(HomeAssistantAddon.OptionsPath));

// App version, from <Version> in BAMF.csproj. Builds may append a source
// revision as "1.0.0+abc1234" — keep just the version itself. Computed before
// the container is built because UpdateChecker needs it at construction.
var version = (Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "0.0.0").Split('+')[0];

// UTC build stamp from BAMF.csproj. Between releases the version doesn't change,
// so this is what actually distinguishes one build from another.
var buildDate = Assembly.GetExecutingAssembly()
    .GetCustomAttributes<AssemblyMetadataAttribute>()
    .FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "";

// Run as a Windows Service or systemd service when installed as one;
// each call is a harmless no-op on the other platform or in a console.
builder.Host.UseWindowsService(o => o.ServiceName = "BAMF");
builder.Host.UseSystemd();

builder.Services.AddSingleton<HostStore>();
builder.Services.AddSingleton<OuiLookup>();
builder.Services.AddSingleton(sp => new UpdateChecker(
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<HostStore>(),
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ILogger<UpdateChecker>>(),
    version));
builder.Services.AddSingleton<MdnsListener>();
builder.Services.AddSingleton<TrafficMonitor>();
builder.Services.AddSingleton<PortBlinker>();
builder.Services.AddSingleton<ScannerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScannerService>());
builder.Services.AddSingleton<ReportService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReportService>());
builder.Services.AddSingleton<MqttPublisher>();
builder.Services.AddSingleton<SecurityCheck>();
builder.Services.AddSingleton<GreyNoiseCheck>();
builder.Services.AddSingleton<RouterImport>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RouterImport>());
builder.Services.AddSingleton<RuleService>();
builder.Services.AddSingleton<RemoteService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RemoteService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<RuleService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttPublisher>());
builder.Services.AddHttpClient();

var app = builder.Build();

// First line in the Event Log / journal, so "what's actually running?" is answerable.
app.Logger.LogInformation("BAMF {Version} (built {BuildDate} UTC) starting", version, buildDate);

// Port scans go easy on declared gateways as they do on the routing table's.
PortChecker.SetDeclaredGateways(app.Services.GetRequiredService<HostStore>().GetGateways().Select(g => g.Ip));

// ---------- warn about plaintext where it costs you something ----------
// BAMF's own calls (IEEE registry, GitHub update check) are HTTPS. These two
// are the paths where a configuration choice can put data in the clear.
var webhook = app.Configuration["Bamf:WebhookUrl"];
if (!string.IsNullOrWhiteSpace(webhook) &&
    webhook.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
{
    app.Logger.LogWarning(
        "WebhookUrl uses http:// - device names, MACs and IPs will be sent in plaintext. " +
        "Use https:// if the endpoint supports it.");
}

if (!string.IsNullOrEmpty(app.Configuration["Bamf:Password"]))
{
    var urls = app.Configuration["Urls"] ?? "";
    if (!urls.Contains("https://", StringComparison.OrdinalIgnoreCase))
    {
        app.Logger.LogWarning(
            "A password is set but the dashboard is served over http:// - HTTP Basic auth " +
            "sends it base64-encoded, which is encoding, not encryption. Fine on a trusted " +
            "LAN; serve HTTPS if this is reachable from anywhere else (see the README).");
    }
}

// ---------- optional HTTP Basic auth ----------
// Password opens everything. ViewerPassword, if set as well, opens the same
// dashboard to look at but not change: every POST and DELETE is refused, and
// so are the port scans, the only GETs that send packets.
var password = app.Configuration["Bamf:Password"];
var viewerPassword = app.Configuration["Bamf:ViewerPassword"];
var hookToken = app.Configuration["Bamf:HookToken"];
if (!string.IsNullOrEmpty(viewerPassword) && string.IsNullOrEmpty(password))
{
    app.Logger.LogWarning("Bamf:ViewerPassword is set without Bamf:Password, so it does nothing: " +
        "with no main password the dashboard is open to everyone. Set Bamf:Password too.");
    viewerPassword = null;
}
// An inbound webhook may carry the hook token instead of the password.
static bool HookTokenOk(HttpContext ctx, string? token) =>
    !string.IsNullOrEmpty(token) && ctx.Request.Path.StartsWithSegments("/api/hooks") &&
    (CryptographicEquals(ctx.Request.Headers["X-BAMF-Token"].ToString(), token) || CryptographicEquals(ctx.Request.Query["token"].ToString(), token));
if (!string.IsNullOrEmpty(password))
{
    app.Use(async (ctx, next) =>
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        string? role = HookTokenOk(ctx, hookToken) ? "admin" : null;
        if (role is null && header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
                var idx = decoded.IndexOf(':');
                var provided = idx >= 0 ? decoded[(idx + 1)..] : "";
                if (CryptographicEquals(provided, password)) role = "admin";
                else if (!string.IsNullOrEmpty(viewerPassword) && CryptographicEquals(provided, viewerPassword)) role = "viewer";
            }
            catch { }
        }

        if (role is null)
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"BAMF\"";
            await ctx.Response.WriteAsync("Authentication required");
            return;
        }
        ctx.Items["bamfRole"] = role;
        if (role == "viewer" && (!(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
                                 || ctx.Request.Path.Value?.Contains("/portscan", StringComparison.OrdinalIgnoreCase) == true))
        {
            ctx.Response.StatusCode = 403;
            ctx.Response.Headers["X-BAMF-ViewOnly"] = "1";
            await ctx.Response.WriteAsJsonAsync(new { error = "View-only: this password can look at everything but not change anything." });
            return;
        }
        await next();
    });
}

app.UseDefaultFiles();
app.UseStaticFiles();

// ---------- drop-in themes ----------
// Each folder in <install>/themes with a theme.json is a theme; see DropInThemes.
var themesDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, app.Configuration["Bamf:ThemesPath"] ?? "themes"));
// The wall display, /wall: a big-screen status board with no controls, for a
// TV or a spare tablet. Static, so it sits behind the same Basic auth as the
// dashboard.
app.MapGet("/wall", () =>
    Results.File(Path.Combine(app.Environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"), "wall.html"), "text/html"));

app.MapGet("/api/themes", () => Results.Json(DropInThemes.List(themesDir).Select(t => new
{
    id = t.Id, name = t.Name, swatch = t.Swatch, css = t.HasCss, js = t.HasJs,
})));
app.MapGet("/themes/{id}/{file}", (string id, string file, HttpContext ctx) =>
{
    var hit = DropInThemes.Resolve(themesDir, id, file);
    if (hit is null) return Results.NotFound();
    // Edits to a theme should show on the next reload, not after a cache expires.
    ctx.Response.Headers.CacheControl = "no-cache";
    return Results.File(hit.Value.Path, hit.Value.ContentType);
});

// ---------- API ----------

app.MapGet("/api/hosts", (HttpContext ctx, HostStore store, ScannerService scanner, UpdateChecker updates, PortBlinker blinker, RemoteService remotesSvc) =>
{
    var linkTemplate = app.Configuration["Bamf:DeviceLinkTemplate"];
    var placements = store.GetPlacements();
    var deviceTypes = store.GetDeviceTypes();
    var addresses = store.GetAddresses();
    var interfaces = store.GetInterfaces();
    var latency = store.LatestLatency();
    var uptimes = store.Uptimes();
    var tags = store.GetTags();
    var counters = scanner.Traffic.Counters();
    var dnsByDevice = scanner.Traffic.DnsByDevice();
    var openPorts = store.OpenPorts();
    var routerNames = store.GetRouterNames();
    var ipv6 = store.GetIpv6(DateTime.UtcNow.AddDays(-7));
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
        traffic = counters.TryGetValue(h.Mac, out var tc) ? new { rx = Math.Round(tc.Rx), tx = Math.Round(tc.Tx), rxTotal = tc.RxTotal, txTotal = tc.TxTotal } : null,
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
    var deviceTypes = store.GetDeviceTypes();
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
        deviceTypes.TryGetValue(h.Id, out var dt) ? $"{dt} (your type)" : h.OsGuess == "" ? "-" : h.OsGuess,
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

// Alerts BAMF raised: rules, ports, DHCP and DNS, newest first.
app.MapGet("/api/alerts", (HostStore store, ScannerService scanner) =>
{
    var mine = store.GetAlerts(50).Select(a => new { at = a.At, kind = a.Kind, title = a.Title, detail = a.Detail });
    var watch = scanner.Traffic.Alerts().Select(a => new { at = a.At, kind = a.Kind, title = a.Title, detail = a.Detail });
    return Results.Json(mine.Concat(watch).OrderByDescending(a => a.at).Take(50));
});

// Alert rules, and quiet hours.
app.MapGet("/api/settings/rules", (RuleService rules, ScannerService scanner) => Results.Json(RulesJson(rules, scanner)));
app.MapPost("/api/settings/rules", (List<RuleService.Rule> body, RuleService rules, ScannerService scanner) =>
{
    var error = rules.SaveRules(body ?? new());
    return error is null ? Results.Json(RulesJson(rules, scanner)) : Results.BadRequest(new { error });
});
app.MapPost("/api/settings/quiet", (QuietRequest body, HostStore store, RuleService rules, ScannerService scanner) =>
{
    var from = (body.From ?? "").Trim(); var to = (body.To ?? "").Trim();
    if ((from != "" || to != "") && (!TimeOnly.TryParse(from, out _) || !TimeOnly.TryParse(to, out _)))
        return Results.BadRequest(new { error = "Give quiet hours a from and to time, like 23:00 and 07:00." });
    store.SetSetting("quietFrom", from); store.SetSetting("quietTo", to);
    store.SetSetting("quietDigest", body.Digest ? "true" : "false");
    return Results.Json(RulesJson(rules, scanner));
});
app.MapPost("/api/settings/port-watch", (ActiveArpRequest body, HostStore store) =>
{
    store.SetSetting("portWatch", body.Enabled ? "true" : "false");
    return Results.Ok();
});
// Runs the port watch now.
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
app.MapGet("/api/traffic", (HostStore store, ScannerService scanner) =>
{
    var t = scanner.Traffic;
    var hosts = store.GetAll().Where(h => !h.Forgotten).ToDictionary(h => h.Mac, h => h, StringComparer.OrdinalIgnoreCase);
    string Name(string mac) => hosts.TryGetValue(mac, out var h) ? (h.CustomName != "" ? h.CustomName : h.Hostname != "" ? h.Hostname : h.Ip) : mac;
    var top = t.Counters()
        .Where(kv => hosts.ContainsKey(kv.Key) || kv.Value.RxTotal + kv.Value.TxTotal > 0)
        .OrderByDescending(kv => kv.Value.RxTotal + kv.Value.TxTotal)
        .Take(12)
        .Select(kv => new
        {
            mac = kv.Key, hostId = hosts.TryGetValue(kv.Key, out var h) ? h.Id : 0, name = Name(kv.Key),
            ip = hosts.TryGetValue(kv.Key, out var h2) ? h2.Ip : "",
            rxTotal = kv.Value.RxTotal, txTotal = kv.Value.TxTotal, rx = Math.Round(kv.Value.Rx), tx = Math.Round(kv.Value.Tx), strip = kv.Value.Strip,
        });
    return Results.Json(new
    {
        status = TrafficJson(scanner),
        top,
        dhcp = t.DhcpServers().Select(s => new { s.Ip, s.Mac, name = Name(s.Mac), s.LastSeen, s.Offers, trusted = t.KnownDhcp.Contains(s.Ip) }),
        dns = t.DnsServers().Select(s => new { s.Ip, s.LastSeen, s.Clients, s.Queries, trusted = t.KnownDns.Contains(s.Ip),
            name = hosts.Values.FirstOrDefault(h => h.Ip == s.Ip) is { } dh ? (dh.CustomName != "" ? dh.CustomName : dh.Hostname) : "" }),
        alerts = t.Alerts(),
    });
});

// Trust a DHCP or DNS server (so it never alerts), or forget it (so it's new again).
app.MapPost("/api/traffic/trust", (TrustRequest body, ScannerService scanner) =>
{
    var kind = (body.Kind ?? "").ToLowerInvariant();
    if (kind is not ("dhcp" or "dns") || !System.Net.IPAddress.TryParse(body.Ip ?? "", out _))
        return Results.BadRequest(new { error = "Say which DHCP or DNS server." });
    scanner.Traffic.Trust(kind, body.Ip!, body.Trusted);
    return Results.Ok();
});

// The recorded layout as one file, and back again. Export names devices by MAC
// and switches by their place in the file, so it means the same on another server.
app.MapGet("/api/layout", (HostStore store) =>
{
    var json = store.ExportLayout(version).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    return Results.Text(json, "application/json", System.Text.Encoding.UTF8);
});
app.MapPost("/api/layout", (System.Text.Json.JsonElement body, HostStore store) =>
{
    var r = store.ImportLayout(body);
    return r.Error is null ? Results.Json(new { r.Devices, r.Switches, r.Skipped }) : Results.BadRequest(new { error = r.Error });
});

// Scheduled reports: off, daily or weekly at an hour of the server's local day.
app.MapPost("/api/settings/report", (ReportRequest body, HostStore store, ReportService reports) =>
{
    var schedule = (body.Schedule ?? "off").ToLowerInvariant();
    if (schedule is not ("off" or "daily" or "weekly")) return Results.BadRequest(new { error = "Schedule is off, daily or weekly." });
    store.SetSetting("reportSchedule", schedule);
    store.SetSetting("reportHour", Math.Clamp(body.Hour ?? 8, 0, 23).ToString());
    store.SetSetting("reportDay", Math.Clamp(body.Day ?? 1, 0, 6).ToString());
    return Results.Json(ReportJson(reports));
});
// Sends the report now, whatever the schedule, and returns what was sent.
app.MapPost("/api/reports/send", async (ReportService reports, ScannerService scanner, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(scanner.WebhookUrl)) return Results.BadRequest(new { error = "No webhook saved. Add one above first." });
    var schedule = reports.Schedule == "weekly" ? "weekly" : "daily";
    var (title, text, _) = reports.Compose(schedule == "weekly" ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1));
    var ok = await reports.SendAsync(schedule, ct);
    return ok ? Results.Json(new { ok = true, title, text }) : Results.Json(new { ok = false, error = "The webhook endpoint didn't accept it.", title, text });
});
// The report as it would be sent, for a look before turning it on.
app.MapGet("/api/reports/preview", (ReportService reports) =>
{
    var (title, text, _) = reports.Compose(reports.Schedule == "weekly" ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1));
    return Results.Json(new { title, text });
});

app.MapPost("/api/settings/traffic-monitor", (ActiveArpRequest body, HostStore store) =>
{
    store.SetSetting("trafficMonitor", body.Enabled ? "true" : "false");
    return Results.Ok();
});

app.MapPost("/api/settings/latency-probe", (ActiveArpRequest body, HostStore store) =>
{
    store.SetSetting("latencyProbe", body.Enabled ? "true" : "false");
    return Results.Ok();
});

// The security watch: the ARP watch (conflicts, the gateway's MAC), the
// certificates on devices' HTTPS ports, and routers that answer UPnP.
app.MapPost("/api/settings/arp-watch", (ActiveArpRequest body, HostStore store, ScannerService scanner) =>
{
    store.SetSetting("arpWatch", body.Enabled ? "true" : "false");
    scanner.ForgetArpWatchCache();
    return Results.Ok();
});
app.MapPost("/api/settings/cert-watch", (ActiveArpRequest body, HostStore store) =>
{
    store.SetSetting("certWatch", body.Enabled ? "true" : "false");
    return Results.Ok();
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
app.MapPost("/api/settings/ipv6-watch", (ActiveArpRequest body, HostStore store) =>
{
    store.SetSetting("ipv6Watch", body.Enabled ? "true" : "false");
    return Results.Ok();
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

// Floor plans: an image per floor, uploaded as the raw request body, and
// where each device sits on one. PNG, JPEG or WebP only, up to 12 MB: an SVG
// could carry script, and these are served from this origin.
app.MapGet("/api/floors", (HostStore store) => Results.Json(new
{
    floors = store.GetFloors().Select(f => new { id = f.Id, name = f.Name, width = f.Width, height = f.Height, updated = f.Updated, kind = f.Mime == HostStore.PlanMime ? "plan" : "image" }),
    places = store.GetFloorPlaces().Select(p => new { hostId = p.HostId, floorId = p.FloorId, x = p.X, y = p.Y }),
}));
app.MapGet("/api/floors/{id:long}/image", (long id, HostStore store, HttpContext ctx) =>
{
    if (store.GetFloorImage(id) is not { } img) return Results.NotFound();
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
    ctx.Response.Headers.CacheControl = "private, max-age=3600";
    return Results.Bytes(img.Data, img.Mime);
});
app.MapPost("/api/floors", async (string? name, int? width, int? height, HttpContext ctx, HostStore store) =>
{
    var (mime, data, error) = await ReadFloorImage(ctx);
    if (error is not null) return Results.BadRequest(new { error });
    if (width is not (> 0 and <= 20000) || height is not (> 0 and <= 20000)) return Results.BadRequest(new { error = "The image's size didn't come through." });
    var clean = (name ?? "").Trim();
    var id = store.AddFloor(clean == "" ? "Floor" : clean.Length > 40 ? clean[..40] : clean, mime!, data!, width.Value, height.Value);
    return Results.Json(new { id });
});
app.MapPost("/api/floors/{id:long}", async (long id, string? name, int? width, int? height, HttpContext ctx, HostStore store) =>
{
    // A new name (query string), a new image (body), or both.
    string? mime = null; byte[]? data = null;
    if ((ctx.Request.ContentLength ?? 0) > 0)
    {
        (mime, data, var error) = await ReadFloorImage(ctx);
        if (error is not null) return Results.BadRequest(new { error });
        if (width is not (> 0 and <= 20000) || height is not (> 0 and <= 20000)) return Results.BadRequest(new { error = "The image's size didn't come through." });
    }
    var clean = name?.Trim();
    if (clean is { Length: > 40 }) clean = clean[..40];
    return store.UpdateFloor(id, string.IsNullOrEmpty(clean) ? null : clean, mime, data, width ?? 0, height ?? 0) ? Results.Ok() : Results.NotFound();
});
app.MapDelete("/api/floors/{id:long}", (long id, HostStore store) => store.DeleteFloor(id) ? Results.Ok() : Results.NotFound());
app.MapGet("/api/floors/{id:long}/plan", (long id, HostStore store) =>
{
    if (store.GetFloorImage(id) is not { } f || f.Mime != HostStore.PlanMime) return Results.NotFound();
    return Results.Text(System.Text.Encoding.UTF8.GetString(f.Data), "application/json");
});
app.MapPost("/api/floors/plan", async (string? name, HttpContext ctx, HostStore store) =>
{
    var (plan, error) = await ReadFloorPlan(ctx);
    if (error is not null) return Results.BadRequest(new { error });
    var clean = (name ?? "").Trim();
    var id = store.AddFloor(clean == "" ? "Floor" : clean.Length > 40 ? clean[..40] : clean,
        HostStore.PlanMime, System.Text.Encoding.UTF8.GetBytes(plan!.Json), plan.Width, plan.Height);
    return Results.Json(new { id });
});
app.MapPost("/api/floors/{id:long}/plan", async (long id, string? name, HttpContext ctx, HostStore store) =>
{
    var (plan, error) = await ReadFloorPlan(ctx);
    if (error is not null) return Results.BadRequest(new { error });
    var clean = name?.Trim();
    if (clean is { Length: > 40 }) clean = clean[..40];
    return store.UpdateFloor(id, string.IsNullOrEmpty(clean) ? null : clean, HostStore.PlanMime,
        System.Text.Encoding.UTF8.GetBytes(plan!.Json), plan.Width, plan.Height) ? Results.Ok() : Results.NotFound();
});
app.MapPost("/api/floors/{id:long}/places", (long id, FloorPlaceRequest body, HostStore store) =>
    store.PlaceOnFloor(body.HostId, id, body.X, body.Y) ? Results.Ok() : Results.NotFound());
app.MapDelete("/api/floors/places/{hostId:long}", (long hostId, HostStore store) =>
{
    store.RemoveFromFloor(hostId);
    return Results.Ok();
});

// GreyNoise: has this network's public address been seen scanning the internet?
// Off unless switched on; turning it on checks straight away.
app.MapGet("/api/greynoise", (GreyNoiseCheck greynoise) => Results.Json(new { enabled = greynoise.Enabled, result = greynoise.Last }));
app.MapPost("/api/settings/greynoise", async (ActiveArpRequest body, HostStore store, GreyNoiseCheck greynoise, CancellationToken ct) =>
{
    store.SetSetting("greynoise", body.Enabled ? "true" : "false");
    if (body.Enabled) await greynoise.Check(ct);
    return Results.Json(new { enabled = greynoise.Enabled, result = greynoise.Last });
});
app.MapPost("/api/greynoise/check", async (GreyNoiseCheck greynoise, CancellationToken ct) =>
    greynoise.Enabled ? Results.Json(new { enabled = true, result = await greynoise.Check(ct) })
                      : Results.Json(new { error = "The GreyNoise check is off: switch it on in Settings first." }, statusCode: 409));

app.MapGet("/api/security", (HostStore store, ScannerService scanner, SecurityCheck security) => Results.Json(SecurityJson(store, scanner, security)));
// Check now: the common ports of every online known device, a UPnP search and
// every HTTPS certificate, one after the other. About a minute on a home network.
app.MapPost("/api/security/check", async (HostStore store, ScannerService scanner, SecurityCheck security, RuleService rules, GreyNoiseCheck greynoise, CancellationToken ct) =>
{
    var ran = await security.Run(async c => await rules.PortWatch(c), certs: true, upnp: true, ct);
    if (ran && greynoise.Enabled) await greynoise.Check(ct);
    return ran ? Results.Json(SecurityJson(store, scanner, security))
               : Results.Conflict(new { error = "A check is already running." });
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

app.MapPost("/api/hosts/{id:long}/known", (long id, KnownRequest body, HostStore store) =>
    store.SetKnown(id, body.Known) ? Results.Ok() : Results.NotFound());

// Save or clear the webhook endpoint from the dashboard, so it doesn't need a
// config edit and a service restart. Stored in the database, which overrides
// Bamf:WebhookUrl exactly like the other runtime settings.
app.MapPost("/api/settings/webhook", (WebhookRequest body, HostStore store, ScannerService scanner) =>
{
    var url = (body.Url ?? "").Trim();

    // Delivery format travels with the URL. Anything unrecognised means "auto",
    // which is the original behaviour: Discord embed for a Discord URL, generic
    // JSON for everything else.
    var format = (body.Format ?? "auto").Trim().ToLowerInvariant();
    if (format is not ("auto" or "ntfy" or "gotify" or "json" or "discord")) format = "auto";
    store.SetSetting("webhookFormat", format);

    if (url.Length == 0)
    {
        store.SetSetting("webhookUrl", "");
        return Results.Json(new { ok = true, configured = false, masked = (string?)null, format });
    }

    if (url.Length > 500)
        return Results.BadRequest(new { error = "That URL is implausibly long." });
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        return Results.BadRequest(new { error = "Enter a full http:// or https:// URL." });

    store.SetSetting("webhookUrl", url);
    return Results.Json(new
    {
        ok = true,
        configured = true,
        masked = MaskWebhook(url),
        format,
        // Surfaced so the dashboard can say so rather than failing silently later.
        insecure = uri.Scheme == Uri.UriSchemeHttp,
        discord = uri.Host.EndsWith("discord.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.EndsWith("discordapp.com", StringComparison.OrdinalIgnoreCase),
    });
});

app.MapPost("/api/webhook/test", async (ScannerService scanner, CancellationToken ct) =>
{
    var error = await scanner.SendTestNotification(ct);
    return error is null ? Results.Ok(new { ok = true }) : Results.Json(new { ok = false, error });
});

app.MapPost("/api/settings/active-arp", (ActiveArpRequest body, HostStore store) =>
{
    store.SetSetting("activeArpScan", body.Enabled ? "true" : "false");
    return Results.Ok();
});

app.MapPost("/api/settings/update-check", async (ActiveArpRequest body, HostStore store, UpdateChecker updates, CancellationToken ct) =>
{
    store.SetSetting("updateCheck", body.Enabled ? "true" : "false");
    // Turning it on should answer immediately rather than at the next daily window.
    if (body.Enabled) await updates.MaybeCheckAsync(ct);
    return Results.Json(new
    {
        ok = true,
        available = updates.UpdateAvailable,
        latest = updates.LatestVersion,
        url = updates.ReleaseUrl,
    });
});

app.MapPost("/api/settings/auto-ignore-random", (ActiveArpRequest body, HostStore store) =>
{
    store.SetSetting("autoIgnoreRandomizedMacs", body.Enabled ? "true" : "false");
    return Results.Ok();
});

app.MapPost("/api/settings/holiday-spirit", (ActiveArpRequest body, HostStore store) =>
{
    store.SetSetting("holidaySpirit", body.Enabled ? "true" : "false");
    return Results.Ok();
});

app.MapPost("/api/settings/night", (NightRequest body, HostStore store) =>
{
    var clock = new System.Text.RegularExpressions.Regex(@"^([01]\d|2[0-3]):[0-5]\d$");
    var from = body.From ?? "21:00";
    var to = body.To ?? "06:00";
    var theme = (body.Theme ?? "nightstreet").Trim().ToLowerInvariant();
    if (!clock.IsMatch(from) || !clock.IsMatch(to))
        return Results.BadRequest(new { error = "Times must be HH:MM, 24-hour." });
    if (from == to)
        return Results.BadRequest(new { error = "Night has to start and end at different times." });
    if (!System.Text.RegularExpressions.Regex.IsMatch(theme, "^[a-z0-9-]{1,40}$"))
        return Results.BadRequest(new { error = "That isn't a theme id." });
    store.SetSetting("nightMode", body.Enabled ? "true" : "false");
    store.SetSetting("nightFrom", from);
    store.SetSetting("nightTo", to);
    store.SetSetting("nightTheme", theme);
    return Results.Ok();
});

// Everything the Settings tab renders, in one round trip. Split into what the
// dashboard may change and what it may only display: anything that decides which
// networks BAMF is allowed to touch, or that needs a restart to apply, stays in
// appsettings.json on purpose. Subnets in particular is the boundary the wildcard
// port-scan guard depends on - a pattern can only expand across configured
// networks, so letting the UI edit that list would dissolve the guarantee.
app.MapGet("/api/settings", (HostStore store, ScannerService scanner, UpdateChecker updates, IConfiguration cfg, ReportService reports, MqttPublisher mqtt, RuleService rulesSvc, RemoteService remotesSvc2) =>
{
    var overrides = scanner.ReadIntervalOverrides();
    return Results.Ok(new
    {
        editable = new
        {
            scanIntervalSeconds = scanner.ConfiguredDefaultInterval,
            subnetIntervalSeconds = scanner.SubnetIntervals,
            subnetIntervalOverrides = overrides,
            // Paused networks as *saved*, read straight from the store the same way
            // subnetIntervalOverrides is, so the form reflects a Save immediately.
            // scanner.DisabledSubnets is the loop's view and only refreshes on its
            // next pass; showing that here made a just-saved toggle appear to
            // revert until the scanner woke up. Filtered to configured networks,
            // so this is always a subset of readOnly.subnets.
            disabledSubnets = scanner.ReadDisabledSubnets()
                .Where(l => scanner.SubnetLabels.Contains(l, StringComparer.OrdinalIgnoreCase))
                .ToList(),
            pingConcurrency = scanner.ConfiguredConcurrency,
            historyRetentionDays = store.RetentionDays,
            offlineAfterMissedScans = scanner.ConfiguredOfflineMisses,
            mdnsListen = scanner.MdnsEnabled,
            mdnsStatus = new
            {
                running = scanner.Mdns.Running,
                interfaces = scanner.Mdns.Interfaces,
                packets = scanner.Mdns.PacketsSeen,
                error = scanner.Mdns.LastError,
            },
            activeArpScan = scanner.ActiveArpEnabled,
            activeArpAvailable = scanner.NpcapAvailable,
            autoIgnoreRandomizedMacs = scanner.AutoIgnoreRandomEnabled,
            latencyProbe = scanner.LatencyProbeEnabled,
            arpWatch = scanner.ArpWatchEnabled,
            certWatch = store.GetSetting("certWatch") != "false",
            ipv6Watch = scanner.Ipv6WatchEnabled,
            greynoise = store.GetSetting("greynoise") == "true",
            trafficMonitor = scanner.TrafficMonitorEnabled,
            trafficStatus = TrafficJson(scanner),
            report = ReportJson(reports),
            rules = RulesJson(rulesSvc, scanner),
            holidaySpirit = HolidaySpirit(store, app.Configuration),
            night = NightJson(store),
            updateCheck = updates.Enabled,
            webhookConfigured = !string.IsNullOrWhiteSpace(scanner.WebhookUrl),
            webhookMasked = MaskWebhook(scanner.WebhookUrl),
        webhookFormat = scanner.WebhookFormat,
        },
        readOnly = new
        {
            subnets = scanner.SubnetLabels,
            urls = cfg["Urls"] ?? "",
            databasePath = cfg["Bamf:DatabasePath"] ?? "bamf.db",
            autoDownloadOui = cfg.GetValue("Bamf:AutoDownloadOui", true),
            updateRepo = cfg["Bamf:UpdateRepo"] ?? "",
            hookToken = !string.IsNullOrEmpty(cfg["Bamf:HookToken"]),
            viewerPassword = !string.IsNullOrEmpty(cfg["Bamf:ViewerPassword"]) && !string.IsNullOrEmpty(cfg["Bamf:Password"]),
            remotes = remotesSvc2.Statuses,
            mqtt = new
            {
                configured = mqtt.Configured, server = mqtt.Configured ? mqtt.Server : null, connected = mqtt.Connected,
                error = mqtt.LastError, published = mqtt.Published, lastPublish = mqtt.LastPublishUtc?.ToString("o"),
                discovery = cfg.GetValue("Bamf:Mqtt:Discovery", true), topicPrefix = cfg["Bamf:Mqtt:TopicPrefix"] ?? "bamf",
            },
        },
        minIntervalSeconds = ScannerService.MinIntervalSeconds,
    });
});

app.MapPost("/api/settings/scan", (ScanSettingsRequest body, HostStore store, ScannerService scanner) =>
{
    var errors = new List<string>();

    if (body.ScanIntervalSeconds is { } interval)
    {
        if (interval < ScannerService.MinIntervalSeconds)
            errors.Add($"Scan interval must be at least {ScannerService.MinIntervalSeconds} seconds.");
        else
            store.SetSetting("scanIntervalSeconds", interval.ToString());
    }

    if (body.PingConcurrency is { } concurrency)
    {
        if (concurrency is < 1 or > 1024)
            errors.Add("Probe concurrency must be between 1 and 1024.");
        else
            store.SetSetting("pingConcurrency", concurrency.ToString());
    }

    if (body.HistoryRetentionDays is { } days)
    {
        if (days < 1)
            errors.Add("History retention must be at least 1 day.");
        else
            store.SetSetting("historyRetentionDays", days.ToString());
    }

    if (body.OfflineAfterMissedScans is { } misses)
    {
        if (misses is < 1 or > 20)
            errors.Add("Offline after missed scans must be between 1 and 20.");
        else
            store.SetSetting("offlineAfterMissedScans", misses.ToString());
    }

    if (body.MdnsListen is { } mdns)
        store.SetSetting("mdnsListen", mdns ? "true" : "false");

    // Sent whole rather than per-key, so clearing a row in the UI removes the
    // override instead of leaving the previous value behind.
    if (body.SubnetIntervalSeconds is { } map)
    {
        var clean = new Dictionary<string, int>();
        foreach (var (label, secs) in map)
        {
            if (secs < ScannerService.MinIntervalSeconds)
                errors.Add($"{label}: interval must be at least {ScannerService.MinIntervalSeconds} seconds.");
            else
                clean[label] = secs;
        }
        if (errors.Count == 0)
            store.SetSetting("subnetScanIntervalSeconds", JsonSerializer.Serialize(clean));
    }

    // Pausing is the one network-level control the dashboard gets, and it only
    // ever narrows: a label must already be in Bamf:Subnets to be accepted, so
    // the file remains the sole authority on which networks BAMF may touch.
    if (body.DisabledSubnets is { } paused)
    {
        var configured = new HashSet<string>(scanner.SubnetLabels, StringComparer.OrdinalIgnoreCase);
        var clean = new List<string>();
        foreach (var raw in paused)
        {
            var label = (raw ?? "").Trim();
            if (label.Length == 0) continue;
            if (!configured.Contains(label))
                errors.Add($"{label} is not a configured network.");
            else
                clean.Add(label);
        }
        if (errors.Count == 0)
            store.SetSetting("disabledSubnets", JsonSerializer.Serialize(clean));
    }

    if (errors.Count > 0) return Results.BadRequest(new { error = string.Join(" ", errors) });
    return Results.Ok();
});

// Drops every dashboard-saved scan setting so appsettings.json is authoritative
// again. Deliberately does not touch the webhook or the toggles that predate the
// Settings tab - those have their own controls and their own meaning of "off".
app.MapPost("/api/settings/scan/reset", (HostStore store) =>
{
    foreach (var key in new[]
             {
                 "scanIntervalSeconds", "pingConcurrency",
                 "historyRetentionDays", "subnetScanIntervalSeconds", "disabledSubnets",
                 "offlineAfterMissedScans", "mdnsListen",
             })
        store.DeleteSetting(key);
    return Results.Ok();
});

app.MapPost("/api/hosts/{id:long}/watch", (long id, WatchRequest body, HostStore store) =>
    store.SetWatched(id, body.Watched) ? Results.Ok() : Results.NotFound());

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

// ---------- switch layout ----------
// The user's own account of how things are cabled, for the map. BAMF can't
// discover it: ARP shows presence, and budget smart switches don't expose
// their MAC table. Nothing here talks to a switch.

app.MapPost("/api/switches", (SwitchInput body, HostStore store) =>
{
    var (saved, error) = store.SaveSwitch(null, body);
    return saved is null ? Results.BadRequest(new { error }) : Results.Json(SwitchJson(saved, store.GetPortLabels()));
});

app.MapPost("/api/switches/{id:long}", (long id, SwitchInput body, HostStore store) =>
{
    var (saved, error) = store.SaveSwitch(id, body);
    return saved is null ? Results.BadRequest(new { error }) : Results.Json(SwitchJson(saved, store.GetPortLabels()));
});

app.MapDelete("/api/switches/{id:long}", (long id, HostStore store) =>
    store.DeleteSwitch(id) ? Results.Ok() : Results.NotFound());

// Everything on one switch at once, from its Ports dialog: the hosts listed
// are placed on it (moving off any other switch), the rest come off it.
app.MapPost("/api/switches/{id:long}/ports", (long id, SwitchPortsRequest body, HostStore store) =>
{
    var entries = (body.Ports ?? new()).Select(p => (p.HostId, p.Port)).ToList();
    var labels = body.Labels?.Select(l => (l.Port, l.Label ?? "")).ToList();
    var error = store.SetSwitchPorts(id, entries, labels);
    return error is null ? Results.Ok() : Results.BadRequest(new { error });
});

// "Find port": pulse a device's switch-port light so the user can see which
// port it's on. On demand, one device at a time, known private hosts only -
// the same boundary as the port checks, since the dashboard may have no password.
app.MapPost("/api/hosts/{id:long}/blink", (long id, BlinkRequest? body, HostStore store, PortBlinker blinker) =>
{
    var host = store.GetAll().FirstOrDefault(h => h.Id == id);
    if (host is null) return Results.NotFound();
    if (!System.Net.IPAddress.TryParse(host.Ip, out var ip) || !IsPrivateAddress(ip))
        return Results.BadRequest(new { error = "Only devices on a private address can be blinked." });
    var (started, until) = blinker.Start(id, ip, body?.Seconds ?? PortBlinker.DefaultSeconds);
    return Results.Json(new
    {
        ok = true, hostId = id, ip = host.Ip,
        started = started.ToString("o"), until = until.ToString("o"), serverTime = DateTime.UtcNow.ToString("o"),
    });
});

app.MapDelete("/api/blink", (PortBlinker blinker) =>
{
    blinker.Stop();
    return Results.Ok();
});

// Topology Map layout: where the user dragged things, per network card. A null
// position forgets that node, so it goes back to the automatic layout.
app.MapPost("/api/map/positions", (MapPositionsRequest body, HostStore store) =>
{
    var error = store.SaveMapPositions(body.Subnet ?? "", body.Positions ?? new());
    return error is null ? Results.Ok() : Results.BadRequest(new { error });
});

// "Auto-arrange": forget every dragged position on one network's card.
app.MapDelete("/api/map/positions", (string? subnet, HostStore store) =>
{
    if (string.IsNullOrWhiteSpace(subnet)) return Results.BadRequest(new { error = "Which network is this for?" });
    store.ClearMapPositions(subnet);
    return Results.Ok();
});

// A device's type, overriding BAMF's guess for its icon and type chip. Empty
// goes back to the guess.
app.MapPost("/api/hosts/{id:long}/type", (long id, DeviceTypeRequest body, HostStore store) =>
{
    var error = store.SetDeviceType(id, body.Type);
    return error is null ? Results.Ok() : Results.BadRequest(new { error });
});

// Icons for whole guessed types: { "icons": { "Linux": "server", "Printer": "" } }.
// An empty icon goes back to the automatic one.
app.MapPost("/api/settings/type-icons", (TypeIconsRequest body, HostStore store) =>
{
    var error = store.SetTypeIcons(body.Icons ?? new());
    return error is null ? Results.Json(store.GetTypeIcons()) : Results.BadRequest(new { error });
});

// Declares a device the gateway of the network one of its addresses is on,
// or (enabled false) stops declaring it. One gateway per network.
app.MapPost("/api/hosts/{id:long}/gateway", (long id, GatewayRequest body, HostStore store) =>
{
    var error = store.SetGateway(id, body.Ip, body.Enabled);
    if (error is not null) return Results.BadRequest(new { error });
    PortChecker.SetDeclaredGateways(store.GetGateways().Select(g => g.Ip));
    return Results.Ok();
});

// Combines a device into another as one of its network cards; parentId 0
// separates it again.
app.MapPost("/api/hosts/{id:long}/combine", (long id, CombineRequest body, HostStore store) =>
{
    var error = store.SetInterfaceOf(id, body.ParentId);
    return error is null ? Results.Ok() : Results.BadRequest(new { error });
});

// ---------- inbound webhooks ----------
// The way in, for Home Assistant and scripts: ask for a scan, or wake a
// machine by MAC. With Bamf:HookToken set, the token (X-BAMF-Token header or
// ?token=) is required here and also stands in for the password.
bool HookAllowed(HttpContext ctx) => string.IsNullOrEmpty(hookToken) || HookTokenOk(ctx, hookToken);
app.MapPost("/api/hooks/scan", (HttpContext ctx, string? subnet, ScannerService scanner) =>
{
    if (!HookAllowed(ctx)) return Results.Unauthorized();
    if (!string.IsNullOrWhiteSpace(subnet) && !scanner.SubnetLabels.Contains(subnet.Trim(), StringComparer.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = $"{subnet} isn't a configured network." });
    scanner.RequestScan(subnet);
    return Results.Json(new { ok = true, networks = string.IsNullOrWhiteSpace(subnet) ? scanner.SubnetLabels : new[] { subnet.Trim() } });
});
app.MapPost("/api/hooks/wake/{mac}", async (HttpContext ctx, string mac, HostStore store) =>
{
    if (!HookAllowed(ctx)) return Results.Unauthorized();
    var norm = mac.Replace('-', ':').ToUpperInvariant();
    if (norm.Length == 12 && !norm.Contains(':')) norm = string.Join(":", Enumerable.Range(0, 6).Select(i => norm.Substring(i * 2, 2)));
    var host = store.GetAll().FirstOrDefault(h => string.Equals(h.Mac, norm, StringComparison.OrdinalIgnoreCase));
    System.Net.IPAddress? directed = null;
    if (host is not null && host.Subnet.Contains('/'))
    {
        var parts = host.Subnet.Split('/');
        if (System.Net.IPAddress.TryParse(parts[0], out var net) && int.TryParse(parts[1], out var prefix)) directed = WakeOnLan.DirectedBroadcast(net, prefix);
    }
    var ok = await WakeOnLan.WakeAsync(norm, directed);
    return ok ? Results.Json(new { ok = true, mac = norm, known = host is not null }) : Results.BadRequest(new { error = "Could not send the magic packet (bad MAC?)" });
});

// ---------- Prometheus ----------
// Devices, presence, latency, uptime and traffic in the text exposition format.
app.MapGet("/metrics", (HostStore store, ScannerService scanner) =>
{
    static string L(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    var hosts = store.GetAll().Where(h => !h.Ignored && !h.Forgotten).ToList();
    var latency = store.LatestLatency();
    var uptimes = store.Uptimes();
    var counters = scanner.Traffic.Counters();
    var sb = new StringBuilder();
    sb.AppendLine("# HELP bamf_devices_total Devices known on a network.\n# TYPE bamf_devices_total gauge");
    sb.AppendLine("# HELP bamf_devices_online Devices online on a network.\n# TYPE bamf_devices_online gauge");
    foreach (var g in hosts.GroupBy(h => h.Subnet))
    {
        sb.AppendLine($"bamf_devices_total{{network=\"{L(g.Key)}\"}} {g.Count()}");
        sb.AppendLine($"bamf_devices_online{{network=\"{L(g.Key)}\"}} {g.Count(h => h.Online)}");
    }
    sb.AppendLine("# HELP bamf_device_online 1 when the device answered the last scan of its network.\n# TYPE bamf_device_online gauge");
    sb.AppendLine("# HELP bamf_device_latency_ms Round-trip time of the last echo, milliseconds.\n# TYPE bamf_device_latency_ms gauge");
    sb.AppendLine("# HELP bamf_device_uptime_7d_percent Share of the last 7 days the device was online.\n# TYPE bamf_device_uptime_7d_percent gauge");
    sb.AppendLine("# HELP bamf_device_rx_bytes_total Bytes to the device since the traffic monitor started.\n# TYPE bamf_device_rx_bytes_total counter");
    sb.AppendLine("# HELP bamf_device_tx_bytes_total Bytes from the device since the traffic monitor started.\n# TYPE bamf_device_tx_bytes_total counter");
    foreach (var h in hosts)
    {
        var name = h.CustomName != "" ? h.CustomName : h.Hostname != "" ? h.Hostname : h.Ip;
        var labels = $"mac=\"{L(h.Mac)}\",name=\"{L(name)}\",ip=\"{L(h.Ip)}\",network=\"{L(h.Subnet)}\"";
        sb.AppendLine($"bamf_device_online{{{labels}}} {(h.Online ? 1 : 0)}");
        if (latency.TryGetValue(h.Id, out var ms) && ms is not null) sb.AppendLine($"bamf_device_latency_ms{{{labels}}} {ms}");
        if (uptimes.TryGetValue(h.Id, out var up) && up.Week is not null) sb.AppendLine($"bamf_device_uptime_7d_percent{{{labels}}} {up.Week.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (counters.TryGetValue(h.Mac, out var c))
        {
            sb.AppendLine($"bamf_device_rx_bytes_total{{{labels}}} {c.RxTotal}");
            sb.AppendLine($"bamf_device_tx_bytes_total{{{labels}}} {c.TxTotal}");
        }
    }
    sb.AppendLine("# HELP bamf_last_scan_timestamp_seconds When the last scan finished, Unix time.\n# TYPE bamf_last_scan_timestamp_seconds gauge");
    if (scanner.LastScanUtc is { } last) sb.AppendLine($"bamf_last_scan_timestamp_seconds {new DateTimeOffset(last).ToUnixTimeSeconds()}");
    sb.AppendLine("# HELP bamf_traffic_monitor_running 1 while the traffic monitor is capturing.\n# TYPE bamf_traffic_monitor_running gauge");
    sb.AppendLine($"bamf_traffic_monitor_running {(scanner.Traffic.Running ? 1 : 0)}");
    sb.AppendLine("# HELP bamf_info BAMF version.\n# TYPE bamf_info gauge");
    sb.AppendLine($"bamf_info{{version=\"{L(version)}\"}} 1");
    return Results.Text(sb.ToString(), "text/plain; version=0.0.4; charset=utf-8");
});
app.MapGet("/api/prometheus", (HttpContext ctx) => Results.Redirect("/metrics"));

// One device's story, newest first.
app.MapGet("/api/hosts/{id:long}/timeline", (long id, HostStore store) =>
    Results.Json(store.Timeline(id).Select(t => new { at = t.At, kind = t.Kind, text = t.Text })));

// Other BAMF servers being watched, and their devices.
app.MapGet("/api/remotes", (RemoteService remotes) => Results.Json(new { remotes = remotes.Statuses, hosts = remotes.Hosts() }));

// Switch 0 clears the placement; port 0 means "on this switch, port not recorded".
app.MapPost("/api/hosts/{id:long}/plug", (long id, PlugRequest body, HostStore store) =>
{
    var error = store.SetPlacement(id, body.SwitchId, body.Port);
    return error is null ? Results.Ok() : Results.BadRequest(new { error });
});

app.Run();

static object SwitchJson(SwitchRecord s, Dictionary<long, Dictionary<int, string>> labels) => new
{
    id = s.Id, name = s.Name, kind = s.Kind, ports = s.Ports, subnet = s.Subnet, hostId = s.HostId,
    uplink = s.Uplink, uplinkSwitch = s.UplinkSwitch, uplinkPort = s.UplinkPort,
    // For a virtual switch: the device it runs inside.
    runsOn = s.RunsOn,
    // Where each port's cable goes, keyed by port number: { "3": "Living Room" }.
    portLabels = labels.TryGetValue(s.Id, out var l) ? l : new Dictionary<int, string>(),
};

static List<object> SwitchesJson(HostStore store)
{
    var labels = store.GetPortLabels();
    return store.GetSwitches().Select(s => SwitchJson(s, labels)).ToList();
}

// Enough of the URL to recognise which webhook is saved, never enough to use it.
// A Discord URL ends /webhooks/<id>/<token>; the token is the secret.
static object RulesJson(RuleService rules, ScannerService scanner) => new
{
    rules = rules.Rules.Select(r => new { r.Id, r.Name, r.Kind, r.Target, r.Minutes, r.From, r.To, r.Enabled, text = rules.Describe(r) }),
    quiet = new { from = scanner.QuietHours.From, to = scanner.QuietHours.To, digest = scanner.QuietDigest, now = scanner.IsQuietNow(), held = scanner.HeldCount },
    portWatch = rules.PortWatchEnabled,
};

static object ReportJson(ReportService reports) => new
{
    schedule = reports.Schedule, hour = reports.Hour, day = reports.Day,
    lastSent = reports.LastSent?.ToString("o"), next = reports.NextDue()?.ToString("o"),
    timeZone = TimeZoneInfo.Local.StandardName,
};

static object TrafficJson(ScannerService scanner) => new
{
    enabled = scanner.TrafficMonitorEnabled,
    available = TrafficMonitor.Available,
    running = scanner.Traffic.Running,
    interfaces = scanner.Traffic.Interfaces,
    error = scanner.Traffic.LastError,
    since = scanner.Traffic.StartedUtc?.ToString("o"),
    frames = scanner.Traffic.Frames,
};

// What the hygiene card needs beyond /api/hosts: certificates, UPnP, the gateways' MACs.
static object SecurityJson(HostStore store, ScannerService scanner, SecurityCheck security) => new
{
    arpWatch = scanner.ArpWatchEnabled,
    certWatch = security.CertWatchEnabled,
    checkedAt = security.LastCheck,
    busy = security.Busy,
    certs = store.GetCerts().Select(c => new
    {
        hostId = c.HostId, port = c.Port, subject = c.Subject, issuer = c.Issuer,
        notAfter = c.NotAfter == "" ? null : c.NotAfter, selfSigned = c.SelfSigned,
        error = c.Error == "" ? null : c.Error, checkedAt = c.CheckedAt,
    }),
    upnp = security.Upnp,
    gatewayMacs = scanner.GatewayMacSnapshot(),
};

// A floor plan image from the request body: PNG, JPEG or WebP by its first
// bytes, whatever the header claims, and no more than 12 MB.
static async Task<(string? Mime, byte[]? Data, string? Error)> ReadFloorImage(HttpContext ctx)
{
    const int max = 12 * 1024 * 1024;
    if (ctx.Request.ContentLength is > max) return (null, null, "That image is over 12 MB.");
    using var ms = new MemoryStream();
    var buf = new byte[81920];
    int n;
    while ((n = await ctx.Request.Body.ReadAsync(buf)) > 0)
    {
        ms.Write(buf, 0, n);
        if (ms.Length > max) return (null, null, "That image is over 12 MB.");
    }
    var d = ms.ToArray();
    string? mime =
        d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 ? "image/png" :
        d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF ? "image/jpeg" :
        d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P' ? "image/webp" :
        null;
    return mime is null ? (null, null, "Use a PNG, JPEG or WebP image.") : (mime, d, null);
}

// A floor plan drawn in BAMF, as JSON from the request body. Everything is
// checked and then written out again from what was checked, so the file on
// disk is only ever BAMF's own shape - never whatever was posted.
static async Task<(PlanFile? Plan, string? Error)> ReadFloorPlan(HttpContext ctx)
{
    const int max = 512 * 1024;
    if (ctx.Request.ContentLength is > max) return (null, "That plan is too big.");
    using var ms = new MemoryStream();
    var buf = new byte[16384];
    int n;
    while ((n = await ctx.Request.Body.ReadAsync(buf)) > 0)
    {
        ms.Write(buf, 0, n);
        if (ms.Length > max) return (null, "That plan is too big.");
    }
    PlanDoc? doc;
    try { doc = System.Text.Json.JsonSerializer.Deserialize<PlanDoc>(ms.ToArray(), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
    catch { return (null, "That plan couldn't be read."); }
    if (doc is null) return (null, "That plan couldn't be read.");
    if (doc.Width is < 100 or > 8000 || doc.Height is < 100 or > 8000) return (null, "That plan's size is out of range.");
    if (doc.Unit is not ("ft" or "m")) return (null, "Measurements are in feet or metres.");
    if (doc.Step is < 4 or > 400 || doc.PerStep is <= 0 or > 1000) return (null, "That plan's scale is out of range.");
    var items = doc.Items ?? new();
    if (items.Count > 2000) return (null, "That plan has too much in it: 2000 pieces at most.");
    var ok = new List<PlanItem>(items.Count);
    var ptIn = (double[]? p) => p is { Length: 2 } && p.All(v => double.IsFinite(v) && v is >= -10000 and <= 10000);
    foreach (var it in items)
    {
        switch (it.K)
        {
            case "wall" or "door" or "window":
                if (!ptIn(it.A) || !ptIn(it.B)) return (null, "That plan has a piece BAMF can't place.");
                ok.Add(new PlanItem(it.K, it.A, it.B, null, null));
                break;
            case "label":
                if (!ptIn(it.P)) return (null, "That plan has a label BAMF can't place.");
                var t = (it.T ?? "").Trim();
                if (t.Length == 0) continue;
                ok.Add(new PlanItem("label", null, null, it.P, t.Length > 40 ? t[..40] : t));
                break;
            default:
                return (null, "That plan has a piece BAMF doesn't know.");
        }
    }
    var clean = new PlanDoc(1, doc.Width, doc.Height, doc.Unit, doc.Step, doc.PerStep, ok);
    return (new PlanFile(System.Text.Json.JsonSerializer.Serialize(clean), doc.Width, doc.Height), null);
}

// Holiday Spirit, effective: a value saved from Settings wins over appsettings.json.
static bool HolidaySpirit(HostStore store, IConfiguration config) =>
    store.GetSetting("holidaySpirit") is string v ? v == "true" : config.GetValue("Bamf:HolidaySpirit", false);

// Night mode: between two clock times, by each browser's own clock, every
// dashboard wears the night theme. Saved from Settings; the defaults are
// 9 pm to 6 am and Night Street.
static object NightJson(HostStore store) => new
{
    enabled = store.GetSetting("nightMode") == "true",
    from = store.GetSetting("nightFrom") ?? "21:00",
    to = store.GetSetting("nightTo") ?? "06:00",
    theme = store.GetSetting("nightTheme") ?? "nightstreet",
};

static string? MaskWebhook(string? url)
{
    if (string.IsNullOrWhiteSpace(url)) return null;
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "(saved)";

    var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length == 0) return $"{uri.Scheme}://{uri.Host}";

    var last = segments[^1];
    var shown = last.Length <= 6 ? new string('•', last.Length) : last[..3] + new string('•', 8);
    var path = string.Join("/", segments[..^1].Append(shown));
    return $"{uri.Scheme}://{uri.Host}/{path}";
}

// Sorts dotted-quads numerically so .9 comes before .10.
static long IpSortKey(string ip)
{
    if (!System.Net.IPAddress.TryParse(ip, out var addr) ||
        addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        return long.MaxValue;
    var b = addr.GetAddressBytes();
    return ((long)b[0] << 24) | ((long)b[1] << 16) | ((long)b[2] << 8) | b[3];
}

// RFC1918, loopback, link-local, and CGNAT — the ranges a LAN monitor has any
// business probing.
static bool IsPrivateAddress(System.Net.IPAddress ip)
{
    var b = ip.GetAddressBytes();
    return b[0] == 10
        || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
        || (b[0] == 192 && b[1] == 168)
        || b[0] == 127
        || (b[0] == 169 && b[1] == 254)
        || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
}

static bool CryptographicEquals(string a, string b)
{
    var ba = Encoding.UTF8.GetBytes(a);
    var bb = Encoding.UTF8.GetBytes(b);
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        ba.Length == bb.Length ? ba : new byte[bb.Length], bb) && ba.Length == bb.Length;
}

record KnownRequest(bool Known);
record NameRequest(string? Name);
record NoteRequest(string? Note);
record LinkRequest(string? Link);
record PlugRequest(long SwitchId, int Port);
record GatewayRequest(string? Ip, bool Enabled);
record CombineRequest(long ParentId);
record TagsRequest(List<string>? Tags);
record TrustRequest(string? Kind, string? Ip, bool Trusted);
record ReportRequest(string? Schedule, int? Hour, int? Day);
record QuietRequest(string? From, string? To, bool Digest);
record PortEntry(long HostId, int Port);
record BlinkRequest(int? Seconds);
record DeviceTypeRequest(string? Type);
record TypeIconsRequest(Dictionary<string, string?>? Icons);
record MapPositionsRequest(string? Subnet, Dictionary<string, double[]?>? Positions);
record PortLabel(int Port, string? Label);
record SwitchPortsRequest(List<PortEntry>? Ports, List<PortLabel>? Labels);
record WebhookRequest(string? Url, string? Format);
record IgnoreRequest(bool Ignored);
record WatchRequest(bool Watched);
record ForgetRequest(bool Forgotten);
record ActiveArpRequest(bool Enabled);
record RouterNamesApply(bool Overwrite);
record FloorPlaceRequest(long HostId, double X, double Y);
// A plan as it travels: walls, doors and windows as two points each, labels as
// a point and a word, plus the grid that gives them their real-world size.
record PlanItem(string? K, double[]? A, double[]? B, double[]? P, string? T);
record PlanDoc(int V, int Width, int Height, string? Unit, double Step, double PerStep, List<PlanItem>? Items);
record PlanFile(string Json, int Width, int Height);
record NightRequest(bool Enabled, string? From, string? To, string? Theme);
record ScanSettingsRequest(
    int? ScanIntervalSeconds,
    int? PingConcurrency,
    int? HistoryRetentionDays,
    int? OfflineAfterMissedScans,
    bool? MdnsListen,
    Dictionary<string, int>? SubnetIntervalSeconds,
    List<string>? DisabledSubnets);
