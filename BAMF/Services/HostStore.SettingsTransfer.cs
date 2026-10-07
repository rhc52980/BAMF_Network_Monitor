using System.Text.Json;
using System.Text.Json.Nodes;

namespace LanWatch.Services;

/// <summary>
/// The settings that make BAMF behave the way you've set it, as a file you can bring to another BAMF: scan and watch
/// switches, quiet hours, alert rules, reports, backups and the list of other BAMF servers. Left out on purpose:
///   - anything secret (webhook URLs, MQTT and server passwords, tokens), which are typed in again at the other end;
///   - what belongs to this site (its networks, gateways, known DHCP and DNS servers), which differs there;
///   - the devices and their history, which have their own backup;
///   - rules about one device, which name a device by a number that means nothing on another server.
/// </summary>
public partial class HostStore
{
    /// <summary>The settings that are the same kind of thing at any site.</summary>
    public static readonly string[] TransferKeys =
    {
        "activeArpScan", "addressWatch", "alertRetry", "allQuiet", "allQuietHour", "arpWatch", "autoIgnoreRandomizedMacs", "backupHour",
        "backupKeep", "backupNightly", "bulkSelect", "cascadeAlert", "certWatch", "diskAlert", "diskAlertPercent", "dnsWatch",
        "firstWeekReport", "flapAlert", "flapDrops", "flapHold", "flowWatch", "greynoise", "heartbeatEnabled", "heartbeatMinutes",
        "historyRetentionDays", "holidaySpirit", "ipv6Watch", "latencyAlert", "latencyAlertMs", "latencyProbe", "mdnsListen",
        "networkScore", "networkTools", "newDays", "nightFrom", "nightMode", "nightTheme", "nightTo", "offlineAfterMissedScans",
        "pauseAlerts", "pingConcurrency", "portWatch", "quietDigest", "quietFrom", "quietTo", "reportDay", "reportHour",
        "reportSchedule", "restartAlert", "scanIntervalSeconds", "signInAlert", "speedTest", "spikeAlert", "tidyDays", "tidyEnabled",
        "traceAnywhere", "trafficMonitor", "unusualWatch", "updateCheck", "upnpIgd", "wanInterval", "wanQuality", "wanSlow",
        "wanTarget", "wanWatch", "watchAlerts", "webhookFormat", "webhookKinds",
    };

    private const int MaxTransferValue = 2000;
    private static readonly JsonSerializerOptions Camel = new(JsonSerializerDefaults.Web);

    /// <summary>The alert rules that mean something at another site: not the ones about one device.</summary>
    public static List<RuleService.Rule> PortableRules(IEnumerable<RuleService.Rule> rules) =>
        rules.Where(r => !(r.Target ?? "").StartsWith("host:", StringComparison.OrdinalIgnoreCase)).ToList();

    private List<RuleService.Rule> SavedRules()
    {
        try { return JsonSerializer.Deserialize<List<RuleService.Rule>>(GetSetting("alertRules") ?? "[]") ?? new(); }
        catch (JsonException) { return new(); }
    }

    private List<RemoteService.Remote> SavedRemoteList()
    {
        try { return JsonSerializer.Deserialize<List<RemoteService.Remote>>(GetSetting("remotes") ?? "") ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>The portable settings as one JSON document. What isn't set here is left out, so the other end keeps its own.</summary>
    public JsonObject ExportSettings(string version)
    {
        var settings = new JsonObject();
        foreach (var key in TransferKeys)
            if (GetSetting(key) is { } v) settings[key] = v;
        var root = new JsonObject
        {
            ["bamfSettings"] = 1,
            ["version"] = version,
            ["settings"] = settings,
        };
        if (GetSetting("alertRules") is not null)
            root["alertRules"] = JsonSerializer.SerializeToNode(PortableRules(SavedRules()), Camel);
        if (GetSetting("remotes") is not null)
            root["remotes"] = JsonSerializer.SerializeToNode(SavedRemoteList().Select(r => new { name = r.Name, url = r.Url }), Camel);
        return root;
    }

    public sealed record SettingsImport(int Applied, int RulesApplied, int RulesSkipped, int Remotes, IReadOnlyList<string> Ignored, bool RemotesChanged);

    /// <summary>
    /// Applies a document from <see cref="ExportSettings"/>. Settings it doesn't mention are left as they are. Returns an
    /// error, or null with what was done. A remote that's already here keeps its saved password.
    /// </summary>
    public (SettingsImport? Result, string? Error) ImportSettings(JsonElement doc)
    {
        if (doc.ValueKind != JsonValueKind.Object || !doc.TryGetProperty("bamfSettings", out var marker) || marker.ValueKind != JsonValueKind.Number)
            return (null, "That isn't a BAMF settings file. Make one with Export settings.");
        var apply = new Dictionary<string, string>();
        var ignored = new List<string>();
        if (doc.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object)
            foreach (var p in s.EnumerateObject())
            {
                if (!TransferKeys.Contains(p.Name)) { ignored.Add(p.Name); continue; }
                if (p.Value.ValueKind != JsonValueKind.String) return (null, $"\"{p.Name}\" should be text.");
                var v = p.Value.GetString() ?? "";
                if (v.Length > MaxTransferValue || v.Any(char.IsControl)) return (null, $"\"{p.Name}\" isn't a value BAMF would save.");
                apply[p.Name] = v;
            }

        List<RuleService.Rule>? rules = null; var rulesSkipped = 0;
        if (doc.TryGetProperty("alertRules", out var rs) && rs.ValueKind == JsonValueKind.Array)
        {
            try { rules = rs.Deserialize<List<RuleService.Rule>>(Camel) ?? new(); }
            catch (JsonException) { return (null, "The alert rules in that file aren't readable."); }
            var portable = PortableRules(rules);
            rulesSkipped = rules.Count - portable.Count;
            rules = portable;
            if (rules.Count > 100 || rules.Any(r => string.IsNullOrWhiteSpace(r.Id) || r.Id.Length > 64)) return (null, "The alert rules in that file aren't ones BAMF could use.");
        }

        List<RemoteService.Remote>? remotes = null;
        if (doc.TryGetProperty("remotes", out var rm) && rm.ValueKind == JsonValueKind.Array)
        {
            var now = SavedRemoteList();
            remotes = new();
            foreach (var r in rm.EnumerateArray())
            {
                var name = r.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim() : "";
                var url = r.TryGetProperty("url", out var u) ? (u.GetString() ?? "").Trim() : "";
                if (name.Length is 0 or > 40 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    return (null, $"A server in that file has no usable name or address ({(name.Length == 0 ? "no name" : name)}).");
                var pass = RemoteService.KeptPassword(now, name, url, null);
                remotes.Add(new RemoteService.Remote(name, url, string.IsNullOrEmpty(pass) ? null : pass));
            }
            if (remotes.Count > 8) return (null, "BAMF watches up to eight other servers.");
        }

        foreach (var (k, v) in apply) SetSetting(k, v);
        if (rules is not null) SetSetting("alertRules", JsonSerializer.Serialize(rules));
        if (remotes is not null) SetSetting("remotes", JsonSerializer.Serialize(remotes));
        return (new SettingsImport(apply.Count, rules?.Count ?? 0, rulesSkipped, remotes?.Count ?? 0, ignored, remotes is not null), null);
    }
}
