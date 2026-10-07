namespace LanWatch.Services;

/// <summary>
/// Which requests change BAMF's settings, and what to call each in the log of
/// settings changes on the Activity tab. Only the name of what changed is
/// kept, never the values sent, so a password, a token or a webhook URL can't
/// end up in it.
/// </summary>
public static class SettingsLog
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["scan"] = "Scanning",
        ["networks"] = "Networks",
        ["active-arp"] = "Active ARP scanning",
        ["auto-ignore-random"] = "Ignore randomised MACs",
        ["latency-probe"] = "Measure latency",
        ["ipv6-watch"] = "Watch IPv6 neighbours",
        ["traffic-monitor"] = "Traffic monitor",
        ["port-watch"] = "Watch ports daily",
        ["wanwatch"] = "Watch the internet connection",
        ["waninterval"] = "Check the internet every",
        ["wantarget"] = "Address to ping",
        ["wanslow"] = "Call the internet slow",
        ["speedtest"] = "Test the internet speed",
        ["password"] = "A password",
        ["https"] = "HTTPS",
        ["arp-watch"] = "ARP watch",
        ["cert-watch"] = "Certificate watch",
        ["greynoise"] = "GreyNoise check",
        ["address-watch"] = "Public IP address alert",
        ["sign-in-alert"] = "New sign-in address alert",
        ["score"] = "Network score",
        ["all-quiet"] = "The daily all-quiet note",
        ["dns-watch"] = "DNS watch",
        ["wan-quality"] = "Internet quality alerts",
        ["latency-alert"] = "Slow watched device alert",
        ["flow-watch"] = "Where devices talk, and scans",
        ["spike-alert"] = "Bandwidth spike alert",
        ["first-week-report"] = "A new device's first-week report",
        ["webhook"] = "Main webhook",
        ["webhookkinds"] = "What the main webhook sends",
        ["destinations"] = "More places alerts go",
        ["report"] = "Scheduled report",
        ["rules"] = "Alert rules",
        ["trace-anywhere"] = "Ping and trace route to any address",
        ["alert-behaviour"] = "Retrying alerts, grouping by switch, and flapping devices",
        ["disk-alert"] = "Warn when disk space is low",
        ["network-tools"] = "Ping, trace route and DNS lookup",
        ["restart-alert"] = "Alert when BAMF restarts unexpectedly",
        ["bulk"] = "Selecting several devices",
        ["tidy"] = "Tidy up old devices",
        ["heartbeat"] = "Heartbeat to a monitoring service",
        ["pause"] = "The Pause alerts control",
        ["import"] = "Settings imported from a file",
        ["quiet"] = "Quiet hours",
        ["alertnudge"] = "The alerts-off banner",
        ["newdays"] = "How long a device is new",
        ["type-icons"] = "Map icons",
        ["holiday-spirit"] = "Holiday Spirit",
        ["night"] = "Night mode",
        ["update-check"] = "Check GitHub for a newer release",
        ["mqtt"] = "Home Assistant (MQTT)",
        ["remotes"] = "Other BAMF servers",
        ["hooktoken"] = "Inbound webhooks' token",
        ["backup"] = "Nightly backups",
        ["unusual"] = "Notice unusual activity",
        ["backup/copyto"] = "Nightly backups' second copy",
    };

    /// <summary>What a request changes, or null when it isn't a settings change.</summary>
    public static string? Label(string method, string path)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return null;
        var p = path.TrimEnd('/');
        if (p.Equals("/api/setup", StringComparison.OrdinalIgnoreCase)) return "First-run setup";
        if (p.Equals("/api/backup/restore", StringComparison.OrdinalIgnoreCase)) return "Restored from a backup";
        if (p.Equals("/api/backup/saved/restore", StringComparison.OrdinalIgnoreCase)) return "Restored from a backup on this machine";
        if (p.Equals("/api/themes/upload", StringComparison.OrdinalIgnoreCase)) return "A theme added from a file";
        var theme = System.Text.RegularExpressions.Regex.Match(p, "^/api/themes/([a-z0-9-]{1,40})/(install|remove)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (theme.Success) return (theme.Groups[2].Value.Equals("install", StringComparison.OrdinalIgnoreCase) ? "Theme added: " : "Theme removed: ") + theme.Groups[1].Value;
        const string prefix = "/api/settings/";
        if (!p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = p[prefix.Length..];
        var reset = rest.EndsWith("/reset", StringComparison.OrdinalIgnoreCase);
        if (reset) rest = rest[..^"/reset".Length];
        var name = Names.TryGetValue(rest, out var n) ? n : null;
        if (name is null) return null;
        return reset ? name + ": back to appsettings.json" : name;
    }

    /// <summary>Who made a change, as the log shows it.</summary>
    public static string Who(string? role, bool passwordSet) => role switch
    {
        "admin" => "the main password",
        "viewer" => "the view-only password",
        _ => passwordSet ? "an unknown caller" : "no password set",
    };
}
