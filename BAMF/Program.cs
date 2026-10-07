using System.Reflection;
using System.Text;
using LanWatch.Services;
using LanWatch.Api;
using static LanWatch.Api.ApiHelpers;

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

// BAMF's own warnings and errors, kept for the dashboard's Problems card.
var problems = new ProblemLog();
builder.Logging.AddProvider(problems);
builder.Services.AddSingleton(problems);

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
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<OuiLookup>();
builder.Services.AddSingleton(sp => new UpdateChecker(
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<HostStore>(),
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ILogger<UpdateChecker>>(),
    version));
builder.Services.AddSingleton<MdnsListener>();
builder.Services.AddSingleton<TrafficMonitor>();
builder.Services.AddSingleton<MeasuredTraffic>();
builder.Services.AddSingleton<SwitchCounters>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SwitchCounters>());
builder.Services.AddSingleton<PortBlinker>();
builder.Services.AddSingleton<ScannerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScannerService>());
builder.Services.AddHostedService<StartupWatch>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReportService>());
builder.Services.AddSingleton<MqttPublisher>();
builder.Services.AddSingleton<SecurityCheck>();
builder.Services.AddSingleton<GreyNoiseCheck>();
builder.Services.AddSingleton<Heartbeat>();
builder.Services.AddSingleton<DiskHealth>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DiskHealth>());
builder.Services.AddSingleton<NetworkTools>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Heartbeat>());
builder.Services.AddSingleton<WanWatch>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WanWatch>());
builder.Services.AddSingleton<SpeedTest>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SpeedTest>());
builder.Services.AddSingleton<RouterImport>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RouterImport>());
builder.Services.AddSingleton<RuleService>();
builder.Services.AddSingleton<RemoteService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RemoteService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<RuleService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttPublisher>());
builder.Services.AddSingleton<UnusualWatch>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UnusualWatch>());
builder.Services.AddSingleton<FlowWatch>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FlowWatch>());
builder.Services.AddSingleton<NightlyBackup>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NightlyBackup>());
builder.Services.AddHttpClient();

// One-click HTTPS (Settings → Security): while its certificate is beside the
// database, BAMF also listens for HTTPS on Bamf:HttpsPort, alongside HTTP.
// Anything that would keep it from starting (an unreadable certificate, the
// port already taken) is skipped instead, and Settings says why, so BAMF
// always comes up on its HTTP address at least.
var httpsPort = builder.Configuration.GetValue("Bamf:HttpsPort", HttpsCert.DefaultPort);
{
    var urls = builder.Configuration["Urls"] ?? "";
    HttpsCert.FileHttps = urls.Contains("https://", StringComparison.OrdinalIgnoreCase);
    var certPath = HttpsCert.PathFor(builder.Configuration);
    if (File.Exists(certPath) && !HttpsCert.FileHttps)
    {
        using var cert = HttpsCert.Load(certPath);
        if (cert is null) HttpsCert.StartProblem = "The certificate couldn't be read. Make a new one.";
        else if (string.IsNullOrWhiteSpace(urls)) HttpsCert.StartProblem = "Urls isn't set in appsettings.json, so there's no address to add HTTPS beside.";
        else if (!HttpsCert.PortFree(httpsPort)) HttpsCert.StartProblem = $"Port {httpsPort} is in use by something else. Set Bamf:HttpsPort to another.";
        else
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Urls"] = urls.TrimEnd(';') + $";https://0.0.0.0:{httpsPort}",
                ["Kestrel:Certificates:Default:Path"] = certPath,
            });
            HttpsCert.RunningThumbprint = cert.Thumbprint;
        }
    }
}

var app = builder.Build();
// Devices a switch or the router counts keep their history from there, not the capture.
app.Services.GetRequiredService<TrafficMonitor>().CountedElsewhere = app.Services.GetRequiredService<MeasuredTraffic>().Covers;

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

// ---------- sign-in ----------
// See AuthService. With no main password BAMF is open, as it always was. With
// one, a browser is sent to /signin and kept signed in by a cookie; scripts,
// other BAMF servers and Home Assistant send the password with HTTP Basic auth,
// as before. The view-only password opens the same dashboard to look at: every
// POST and DELETE is refused, and so are the port scans, the only GETs that
// send packets, and the database backup, which carries the saved webhook URL a
// viewer only ever sees masked.
var auth = app.Services.GetRequiredService<AuthService>();
// The inbound webhooks' token: set in Settings → System, or Bamf:HookToken.
string? HookToken() =>
    app.Services.GetRequiredService<HostStore>().GetSetting("hookToken") is { Length: > 0 } t ? t : app.Configuration["Bamf:HookToken"] is { Length: > 0 } f ? f : null;
// A forgotten password is reset from the machine BAMF runs on, which already
// has the say over appsettings.json: a file named reset-password beside the
// database clears the passwords set in Settings at the next start, and is
// deleted; or Bamf:ResetPassword (an environment variable in Docker, an option
// in the Home Assistant add-on) does it once each time it's switched on.
{
    var store = app.Services.GetRequiredService<HostStore>();
    var trigger = Path.Combine(Path.GetDirectoryName(store.DatabasePath) ?? AppContext.BaseDirectory, "reset-password");
    if (File.Exists(trigger))
    {
        auth.ResetSettingsPasswords("the reset-password file");
        try { File.Delete(trigger); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            app.Logger.LogWarning("Couldn't delete {File}; delete it, or the passwords are cleared again at every start: {Error}", trigger, ex.Message);
        }
    }
    if (app.Configuration.GetValue("Bamf:ResetPassword", false))
    {
        if (store.GetSetting("passwordResetDone") != "1")
        {
            auth.ResetSettingsPasswords("Bamf:ResetPassword");
            store.SetSetting("passwordResetDone", "1");
        }
        else app.Logger.LogWarning("Bamf:ResetPassword is still on. It has done its job; switch it off, so it can clear a password again next time.");
    }
    else store.DeleteSetting("passwordResetDone");
}
if (!string.IsNullOrEmpty(app.Configuration["Bamf:ViewerPassword"]) && auth.Source("admin") is null)
    app.Logger.LogWarning("Bamf:ViewerPassword is set without a main password, so it does nothing: " +
        "with no main password the dashboard is open to everyone. Set Bamf:Password too, or set both in Settings, under Security.");
if (HttpsCert.StartProblem is { } httpsProblem)
    app.Logger.LogWarning("HTTPS isn't on: {Problem}", httpsProblem);
else if (HttpsCert.RunningThumbprint is not null)
    app.Logger.LogInformation("HTTPS on port {Port}, with the certificate made in Settings", httpsPort);
if (auth.Required && !(app.Configuration["Urls"] ?? "").Contains("https://", StringComparison.OrdinalIgnoreCase))
    app.Logger.LogInformation(
        "A password is set and the dashboard is served over http://, so the password crosses the network " +
        "readable to anyone who can watch it. Fine on a trusted LAN; serve HTTPS if this is reachable from anywhere else (see the README).");
// Someone keeps getting the password wrong: say so, as a security alert.
auth.LockedOut = (address, count) =>
{
    var scanner = app.Services.GetRequiredService<ScannerService>();
    _ = scanner.SendGenericAlert("Wrong passwords",
        $"{count} wrong passwords for BAMF from {address}. That address can't sign in for {(int)AuthService.LockoutTime.TotalMinutes} minutes.",
        "security", CancellationToken.None);
};
// The log of settings changes, on the Activity tab: once a change has gone
// through, what it was (by name, never the values), by which password, and
// from where. Outside the sign-in check, so it sees who that decided it was.
app.Use(async (ctx, next) =>
{
    var passwordSet = auth.Required;   // as it was: setting the first password isn't by "someone"
    await next();
    if (ctx.Response.StatusCode is < 200 or >= 300) return;
    if (SettingsLog.Label(ctx.Request.Method, ctx.Request.Path.Value ?? "") is not { } what) return;
    var from = ctx.Connection.RemoteIpAddress;
    var address = from is null ? "" : (from.IsIPv4MappedToIPv6 ? from.MapToIPv4() : from).ToString();
    try { app.Services.GetRequiredService<HostStore>().LogSettingsChange(what, SettingsLog.Who(ctx.Items["bamfRole"] as string, passwordSet), address); }
    catch (Exception ex) { app.Logger.LogWarning("Couldn't log a settings change: {Error}", ex.Message); }
});

app.Use(async (ctx, next) =>
{
    if (!auth.Required) { await next(); return; }
    var address = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
    string? role = ctx.Request.Path.StartsWithSegments("/api/hooks") && HookTokenOk(ctx, HookToken()) ? "admin" : null;

    // A browser that has signed in. A session over half gone is renewed, so
    // a dashboard that's in use stays signed in.
    if (role is null && auth.Validate(ctx.Request.Cookies[AuthService.CookieName]) is { } session)
    {
        role = session.Role;
        if (session.Expires - DateTime.UtcNow < AuthService.SessionLife / 2) SetSessionCookie(ctx, auth.Issue(role));
    }

    var header = ctx.Request.Headers.Authorization.ToString();
    if (role is null && header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
        if (auth.IsLocked(address, out var left))
        {
            await TooManyTries(ctx, left);
            return;
        }
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
            var idx = decoded.IndexOf(':');
            role = auth.RoleFor(idx >= 0 ? decoded[(idx + 1)..] : "");
        }
        catch (FormatException) { }
        if (role is null) auth.Failed(address); else auth.Succeeded(address);
    }

    if (role is null)
    {
        if (OpenPath(ctx.Request.Path)) { await next(); return; }
        // A browser opening a page goes to the sign-in page, and one of the
        // dashboard's own requests is told to; anything else (a script,
        // another BAMF, curl) is asked for Basic auth, as before.
        var mode = ctx.Request.Headers["Sec-Fetch-Mode"].ToString();
        var navigating = mode == "navigate" || (mode.Length == 0 && HttpMethods.IsGet(ctx.Request.Method)
            && ctx.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase));
        if (navigating)
        {
            var back = ctx.Request.Path + ctx.Request.QueryString;
            ctx.Response.Redirect("/signin" + (back is "/" or "/index.html" ? "" : "?next=" + Uri.EscapeDataString(back)));
            return;
        }
        ctx.Response.StatusCode = 401;
        if (mode.Length > 0)
        {
            ctx.Response.Headers["X-BAMF-SignIn"] = "1";
            await ctx.Response.WriteAsJsonAsync(new { error = "Sign in to BAMF." });
        }
        else
        {
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"BAMF\"";
            await ctx.Response.WriteAsync("Authentication required");
        }
        return;
    }
    ctx.Items["bamfRole"] = role;
    if (role == "viewer" && (!(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
                             || ctx.Request.Path.Value?.Contains("/portscan", StringComparison.OrdinalIgnoreCase) == true
                             || ctx.Request.Path.StartsWithSegments("/api/backup"))
        && ctx.Request.Path != "/api/signout")
    {
        ctx.Response.StatusCode = 403;
        ctx.Response.Headers["X-BAMF-ViewOnly"] = "1";
        await ctx.Response.WriteAsJsonAsync(new { error = ctx.Request.Path.StartsWithSegments("/api/backup")
            ? "View-only: a backup carries the saved webhook URL, so it takes the main password."
            : "View-only: this password can look at everything but not change anything." });
        return;
    }
    await next();
});

// ---------- one-click HTTPS, in Settings → Security ----------
object HttpsJson()
{
    var path = HttpsCert.PathFor(app.Configuration);
    using var cert = HttpsCert.Load(path);
    var running = HttpsCert.RunningThumbprint;
    // "file": appsettings.json serves HTTPS itself. "on": this certificate is
    // being served. "restart": made or removed since BAMF started. "problem":
    // there, but not served. "off": none.
    var unreadable = cert is null && File.Exists(path);
    var state = HttpsCert.FileHttps ? "file"
        : unreadable ? "problem"
        : cert is not null && running == cert.Thumbprint ? "on"
        : cert is not null && running is null && HttpsCert.StartProblem is not null ? "problem"
        : cert is null && running is null ? "off"
        : "restart";
    return new
    {
        state,
        port = httpsPort,
        problem = state != "problem" ? null : unreadable ? "The certificate couldn't be read. Make a new one." : HttpsCert.StartProblem,
        certificate = cert is null ? null : new
        {
            names = HttpsCert.NamesIn(cert),
            expires = cert.NotAfter.ToUniversalTime().ToString("o"),
            fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.RawData)),
        },
        restart = HttpsCert.RestartHint(File.Exists(HomeAssistantAddon.OptionsPath), httpsPort),
    };
}

app.UseDefaultFiles();
// The dashboard is a page and the scripts and stylesheet it loads. A browser
// checks each with the server before using its copy (a quick 304 when it
// hasn't changed), so after an update it can't run an old script in the new
// page, or the other way round.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (Path.GetExtension(ctx.File.Name) is ".html" or ".js" or ".css" or ".json" or ".webmanifest")
            ctx.Context.Response.Headers.CacheControl = "no-cache";
    },
});

// ---------- themes ----------
// Each folder in <install>/themes with a theme.json is a theme; see DropInThemes.
// The ones BAMF comes with are in <install>/theme-library, and are copied into
// the themes folder at startup unless they've been removed; see ThemeLibrary.
var themesDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, app.Configuration["Bamf:ThemesPath"] ?? "themes"));
var themeLib = new ThemeLibrary(themesDir, Path.Combine(AppContext.BaseDirectory, "theme-library"), app.Logger);
themeLib.Sync();
themeLib.ImportZips();

// ---------- inbound webhooks ----------
// The way in, for Home Assistant and scripts: ask for a scan, or wake a
// machine by MAC. With Bamf:HookToken set, the token (X-BAMF-Token header or
// ?token=) is required here and also stands in for the password.
bool HookAllowed(HttpContext ctx) => HookToken() is not { } token || HookTokenOk(ctx, token);

// The endpoints, by what they're for: BAMF/Api/*Endpoints.cs.
SignInEndpoints.Map(app, auth);
SetupEndpoints.Map(app, auth);
HttpsEndpoints.Map(app, HttpsJson);
ThemeEndpoints.Map(app, themesDir, themeLib);
FloorEndpoints.Map(app);
InternetEndpoints.Map(app, version);
ConnectionsEndpoints.Map(app);
SecurityEndpoints.Map(app);
LayoutEndpoints.Map(app);
IntegrationEndpoints.Map(app, version, HookAllowed);
AlertEndpoints.Map(app);
SettingsEndpoints.Map(app, version, HookToken, HttpsJson);
DeviceEndpoints.Map(app, version, buildDate);
UnusualEndpoints.Map(app);
FlowEndpoints.Map(app);

app.Run();
