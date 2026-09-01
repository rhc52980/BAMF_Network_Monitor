using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using LanWatch.Services;

var builder = WebApplication.CreateBuilder(args);

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

// When running as a service the working directory is System32 — anchor content root to the exe.
builder.Host.UseContentRoot(AppContext.BaseDirectory);

builder.Services.AddSingleton<HostStore>();
builder.Services.AddSingleton<OuiLookup>();
builder.Services.AddSingleton(sp => new UpdateChecker(
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<HostStore>(),
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ILogger<UpdateChecker>>(),
    version));
builder.Services.AddSingleton<ScannerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScannerService>());
builder.Services.AddHttpClient();

var app = builder.Build();

// First line in the Event Log / journal, so "what's actually running?" is answerable.
app.Logger.LogInformation("BAMF {Version} (built {BuildDate} UTC) starting", version, buildDate);

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
var password = app.Configuration["Bamf:Password"];
if (!string.IsNullOrEmpty(password))
{
    app.Use(async (ctx, next) =>
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var ok = false;
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
                var idx = decoded.IndexOf(':');
                var provided = idx >= 0 ? decoded[(idx + 1)..] : "";
                ok = CryptographicEquals(provided, password);
            }
            catch { }
        }

        if (!ok)
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"BAMF\"";
            await ctx.Response.WriteAsync("Authentication required");
            return;
        }
        await next();
    });
}

app.UseDefaultFiles();
app.UseStaticFiles();

// ---------- API ----------

app.MapGet("/api/hosts", (HostStore store, ScannerService scanner, UpdateChecker updates) =>
{
    var linkTemplate = app.Configuration["Bamf:DeviceLinkTemplate"];
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
        link = h.Link,                                              // raw override, for editing
        linkUrl = DeviceLink.Resolve(h.Link, h.Ip, linkTemplate),   // resolved, for the href
        firstSeen = h.FirstSeen,
        lastSeen = h.LastSeen,
    });
    return Results.Json(new
    {
        version,
        buildDate,
        subnets = scanner.SubnetLabels,
        scanModes = scanner.SubnetModes,
        activeArp = new { enabled = scanner.ActiveArpEnabled, npcapAvailable = scanner.NpcapAvailable },
        autoIgnoreRandom = scanner.AutoIgnoreRandomEnabled,
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
        lastScan = scanner.LastScanUtc?.ToString("o"),
        scanIntervalSeconds = scanner.ScanIntervalSeconds,
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

    var rows = hosts.Select(h => new[]
    {
        h.CustomName != "" ? h.CustomName : (h.Hostname != "" ? h.Hostname : "-"),
        h.Ip,
        h.Mac,
        h.Vendor == "" ? "-" : h.Vendor,
        h.Subnet == "" ? "-" : h.Subnet,
        h.Online ? "online" : "offline",
        string.Concat(h.Known ? "K" : "-", h.Ignored ? "I" : "-", h.Watched ? "W" : "-", h.Forgotten ? "F" : "-"),
        h.OsGuess == "" ? "-" : h.OsGuess,
        h.LastSeen,
        h.Note == "" ? "-" : h.Note,
    }).ToList();

    string[] headers = { "NAME", "IP", "MAC", "VENDOR", "NETWORK", "STATUS", "FLAGS", "DEVICE GUESS", "LAST SEEN", "NOTE" };
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

app.MapGet("/api/hosts/{id:long}/portscan", async (long id, string? ports, HostStore store, CancellationToken ct) =>
{
    var host = store.GetAll().FirstOrDefault(h => h.Id == id);
    if (host is null) return Results.NotFound();
    var spec = PortChecker.ParseSpec(ports);
    var open = spec is null
        ? await PortChecker.ScanAsync(host.Ip, 700, ct)
        : await PortChecker.ScanAsync(host.Ip, spec, 700, ct);
    return Results.Json(open.Select(o => new { port = o.Port, service = o.Service }));
});

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
    foreach (var label in scanner.SubnetLabels)
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
        return Results.BadRequest(new { error = $"'{ip}' matches no address on {(scanner.SubnetLabels.Count == 0 ? "any configured network" : string.Join(", ", scanner.SubnetLabels))}." });
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
app.MapGet("/api/portscan", async (string? ports, string? subnet, HostStore store, CancellationToken ct) =>
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

app.MapGet("/api/hosts/{id:long}/events", (long id, HostStore store) =>
    Results.Json(store.GetEvents(id).Select(e => new { type = e.Type, at = e.At })));

app.MapPost("/api/hosts/{id:long}/known", (long id, KnownRequest body, HostStore store) =>
    store.SetKnown(id, body.Known) ? Results.Ok() : Results.NotFound());

// Save or clear the webhook endpoint from the dashboard, so it doesn't need a
// config edit and a service restart. Stored in the database, which overrides
// Bamf:WebhookUrl exactly like the other runtime settings.
app.MapPost("/api/settings/webhook", (WebhookRequest body, HostStore store, ScannerService scanner) =>
{
    var url = (body.Url ?? "").Trim();

    if (url.Length == 0)
    {
        store.SetSetting("webhookUrl", "");
        return Results.Json(new { ok = true, configured = false, masked = (string?)null });
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

app.Run();

// Enough of the URL to recognise which webhook is saved, never enough to use it.
// A Discord URL ends /webhooks/<id>/<token>; the token is the secret.
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
record WebhookRequest(string? Url);
record IgnoreRequest(bool Ignored);
record WatchRequest(bool Watched);
record ForgetRequest(bool Forgotten);
record ActiveArpRequest(bool Enabled);
