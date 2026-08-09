using System.Reflection;
using System.Text;
using LanWatch.Services;

var builder = WebApplication.CreateBuilder(args);

// Run as a Windows Service or systemd service when installed as one;
// each call is a harmless no-op on the other platform or in a console.
builder.Host.UseWindowsService(o => o.ServiceName = "BAMF");
builder.Host.UseSystemd();

// When running as a service the working directory is System32 — anchor content root to the exe.
builder.Host.UseContentRoot(AppContext.BaseDirectory);

builder.Services.AddSingleton<HostStore>();
builder.Services.AddSingleton<OuiLookup>();
builder.Services.AddSingleton<ScannerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScannerService>());
builder.Services.AddHttpClient();

var app = builder.Build();

// App version, from <Version> in BAMF.csproj. Builds may append a source
// revision as "1.0.0+abc1234" — keep just the version itself.
var version = (Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "0.0.0").Split('+')[0];

// First line in the Event Log / journal, so "what's actually running?" is answerable.
app.Logger.LogInformation("BAMF {Version} starting", version);

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

app.MapGet("/api/hosts", (HostStore store, ScannerService scanner) =>
{
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
        firstSeen = h.FirstSeen,
        lastSeen = h.LastSeen,
    });
    return Results.Json(new
    {
        version,
        subnets = scanner.SubnetLabels,
        scanModes = scanner.SubnetModes,
        activeArp = new { enabled = scanner.ActiveArpEnabled, npcapAvailable = scanner.NpcapAvailable },
        autoIgnoreRandom = scanner.AutoIgnoreRandomEnabled,
        webhookConfigured = !string.IsNullOrWhiteSpace(app.Configuration["Bamf:WebhookUrl"]),
        lastScan = scanner.LastScanUtc?.ToString("o"),
        scanIntervalSeconds = scanner.ScanIntervalSeconds,
        hosts,
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
    // Scan hosts a few at a time so we don't blast the whole subnet at once.
    using var sem = new SemaphoreSlim(8);
    var tasks = hosts.Select(async h =>
    {
        await sem.WaitAsync(ct);
        try
        {
            var open = spec is null
                ? await PortChecker.ScanAsync(h.Ip, 700, ct)
                : await PortChecker.ScanAsync(h.Ip, spec, 700, ct);
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
record IgnoreRequest(bool Ignored);
record WatchRequest(bool Watched);
record ForgetRequest(bool Forgotten);
record ActiveArpRequest(bool Enabled);
