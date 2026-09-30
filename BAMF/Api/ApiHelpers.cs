using System.Text;
using System.Text.Json;
using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>What the endpoints share: turning things into JSON, reading uploads, small checks.</summary>
internal static class ApiHelpers
{
    // An inbound webhook may carry the hook token instead of the password.
    internal static bool HookTokenOk(HttpContext ctx, string? token) =>
        !string.IsNullOrEmpty(token) && ctx.Request.Path.StartsWithSegments("/api/hooks") &&
        (CryptographicEquals(ctx.Request.Headers["X-BAMF-Token"].ToString(), token) || CryptographicEquals(ctx.Request.Query["token"].ToString(), token));

    // What the sign-in page needs, open to anyone.
    internal static bool OpenPath(PathString p) =>
        p == "/signin" || p == "/api/signin" || p == "/api/signout" || p == "/fonts.css" || p == "/bamf-logo.svg" || p == "/bamf-icon.svg"
        || p.StartsWithSegments("/fonts")
        // The home-screen icon and its manifest, which a phone fetches without the sign-in cookie.
        || p == "/manifest.webmanifest" || p == "/apple-touch-icon.png" || p == "/icon-192.png" || p == "/icon-512.png" || p == "/icon-maskable-512.png";

    internal static void SetSessionCookie(HttpContext ctx, string? value) =>
        ctx.Response.Cookies.Append(AuthService.CookieName, value ?? "", new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, Path = "/",
            Expires = value is null ? DateTimeOffset.UnixEpoch : DateTimeOffset.UtcNow.Add(AuthService.SessionLife),
        });

    internal static async Task TooManyTries(HttpContext ctx, TimeSpan left)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes));
        ctx.Response.StatusCode = 429;
        ctx.Response.Headers.RetryAfter = ((int)left.TotalSeconds).ToString();
        await ctx.Response.WriteAsJsonAsync(new { error = $"Too many wrong passwords. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}." });
    }

    // How long a device stays under the New tab, unless it's marked known first.
    // Saved on the server, so every dashboard agrees on what's new.
    internal static int NewDays(HostStore store) => int.TryParse(store.GetSetting("newDays"), out var d) && d is >= 1 and <= 90 ? d : 7;

    // The networks BAMF scans, where that list comes from, and the ones this
    // machine is on, for Settings → Scanning → Networks and the first-run setup.
    internal static object NetworksJson(ScannerService scanner) => new
    {
        source = scanner.NetworkSource,
        list = scanner.CurrentNetworks(),
        found = ScannerService.DetectLocalNetworks().Select(n => new
        {
            network = $"{n.Network}/{n.Prefix}", @interface = n.Interface,
            problem = ScannerService.CheckNetwork($"{n.Network}/{n.Prefix}", out _),
        }),
        widest = ScannerService.WidestPrefix,
    };

    // MQTT, other BAMF servers and the inbound webhooks' token, each replacing
    // appsettings.json's once saved. Their passwords are write-only: never sent
    // back, kept when a save leaves them out, and dropped when the address they
    // go to changes, so the dashboard can't be used to send a saved password to a
    // server of someone else's choosing.
    internal static object MqttJson(MqttPublisher mqtt)
    {
        var c = mqtt.Current();
        return new
        {
            source = mqtt.Source,
            server = c?.Server ?? "", port = c?.Port ?? 1883, username = c?.Username ?? "", password = !string.IsNullOrEmpty(c?.Password),
            tls = c?.Tls ?? false, discovery = c?.Discovery ?? true, topicPrefix = c?.TopicPrefix ?? "bamf",
            connected = mqtt.Connected, error = mqtt.LastError, published = mqtt.Published, lastPublish = mqtt.LastPublishUtc?.ToString("o"),
        };
    }

    internal static object SwitchJson(SwitchRecord s, Dictionary<long, Dictionary<int, string>> labels) => new
    {
        id = s.Id, name = s.Name, kind = s.Kind, ports = s.Ports, subnet = s.Subnet, hostId = s.HostId,
        uplink = s.Uplink, uplinkSwitch = s.UplinkSwitch, uplinkPort = s.UplinkPort,
        // For a virtual switch: the device it runs inside.
        runsOn = s.RunsOn,
        // Where each port's cable goes, keyed by port number: { "3": "Living Room" }.
        portLabels = labels.TryGetValue(s.Id, out var l) ? l : new Dictionary<int, string>(),
    };

    internal static List<object> SwitchesJson(HostStore store)
    {
        var labels = store.GetPortLabels();
        return store.GetSwitches().Select(s => SwitchJson(s, labels)).ToList();
    }

    // The destinations besides the main webhook, each URL masked like the main one's.
    internal static object DestinationsJson(ScannerService scanner) =>
        scanner.ExtraDestinations.Select(d => new { id = d.Id, name = d.Name, masked = MaskWebhook(d.Url), format = d.Format, kinds = d.Kinds }).ToList();

    // Enough of the URL to recognise which webhook is saved, never enough to use it.
    // A Discord URL ends /webhooks/<id>/<token>; the token is the secret.
    internal static object RulesJson(RuleService rules, ScannerService scanner) => new
    {
        rules = rules.Rules.Select(r => new { r.Id, r.Name, r.Kind, r.Target, r.Minutes, r.From, r.To, r.Enabled, text = rules.Describe(r) }),
        quiet = new { from = scanner.QuietHours.From, to = scanner.QuietHours.To, digest = scanner.QuietDigest, now = scanner.IsQuietNow(), held = scanner.HeldCount },
        portWatch = rules.PortWatchEnabled,
    };

    internal static object ReportJson(ReportService reports) => new
    {
        schedule = reports.Schedule, hour = reports.Hour, day = reports.Day,
        lastSent = reports.LastSent?.ToString("o"), next = reports.NextDue()?.ToString("o"),
        timeZone = TimeZoneInfo.Local.StandardName,
    };

    internal static object TrafficJson(ScannerService scanner) => new
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
    internal static object SecurityJson(HostStore store, ScannerService scanner, SecurityCheck security) => new
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
    internal static async Task<(string? Mime, byte[]? Data, string? Error)> ReadFloorImage(HttpContext ctx)
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
    internal static async Task<(PlanFile? Plan, string? Error)> ReadFloorPlan(HttpContext ctx)
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
    internal static bool HolidaySpirit(HostStore store, IConfiguration config) =>
        store.GetSetting("holidaySpirit") is string v ? v == "true" : config.GetValue("Bamf:HolidaySpirit", false);

    // Night mode: between two clock times, by each browser's own clock, every
    // dashboard wears the night theme. Saved from Settings; the defaults are
    // 9 pm to 6 am and Night Street.
    internal static object NightJson(HostStore store) => new
    {
        enabled = store.GetSetting("nightMode") == "true",
        from = store.GetSetting("nightFrom") ?? "21:00",
        to = store.GetSetting("nightTo") ?? "06:00",
        theme = store.GetSetting("nightTheme") ?? "nightstreet",
    };

    internal static string? MaskWebhook(string? url)
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
    internal static long IpSortKey(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var addr) ||
            addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return long.MaxValue;
        var b = addr.GetAddressBytes();
        return ((long)b[0] << 24) | ((long)b[1] << 16) | ((long)b[2] << 8) | b[3];
    }

    // RFC1918, loopback, link-local, and CGNAT — the ranges a LAN monitor has any
    // business probing.
    internal static bool IsPrivateAddress(System.Net.IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || b[0] == 127
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    internal static bool CryptographicEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            ba.Length == bb.Length ? ba : new byte[bb.Length], bb) && ba.Length == bb.Length;
    }
}
