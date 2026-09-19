using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// Running as a Home Assistant add-on, BAMF gets its options from
/// /data/options.json, which Home Assistant writes from the add-on's
/// Configuration tab. This turns them into the same Bamf:* settings as
/// appsettings.json, layered on top of it.
/// </summary>
public static class HomeAssistantAddon
{
    /// <summary>Where Home Assistant puts the options; BAMF_ADDON_OPTIONS points elsewhere, for testing.</summary>
    public static string OptionsPath => Environment.GetEnvironmentVariable("BAMF_ADDON_OPTIONS") is { Length: > 0 } p ? p : "/data/options.json";

    public static Dictionary<string, string?> Read(string path)
    {
        var map = new Dictionary<string, string?>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var o = doc.RootElement;
        string? Str(string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // The subnets replace the file's list outright: every index the file
        // might use is set, blank past the add-on's own. Blank means scan
        // whatever networks the host is on.
        var subnets = o.TryGetProperty("subnets", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(x => x != "").ToList()
            : new List<string>();
        for (var i = 0; i < 16; i++) map[$"Bamf:Subnets:{i}"] = i < subnets.Count ? subnets[i] : "";

        if (o.TryGetProperty("scan_interval_seconds", out var iv) && iv.TryGetInt32(out var secs)) map["Bamf:ScanIntervalSeconds"] = secs.ToString();
        if (o.TryGetProperty("active_arp", out var arp) && arp.ValueKind is JsonValueKind.True or JsonValueKind.False) map["Bamf:ActiveArpScan"] = arp.GetBoolean().ToString();
        if (o.TryGetProperty("traffic_monitor", out var tm) && tm.ValueKind is JsonValueKind.True or JsonValueKind.False) map["Bamf:TrafficMonitor"] = tm.GetBoolean().ToString();
        if (Str("password") is { } pw) map["Bamf:Password"] = pw;
        if (Str("webhook_url") is { } hook) map["Bamf:WebhookUrl"] = hook;
        if (Str("mqtt_server") is { Length: > 0 } mqtt)
        {
            map["Bamf:Mqtt:Server"] = mqtt;
            if (Str("mqtt_username") is { } mu) map["Bamf:Mqtt:Username"] = mu;
            if (Str("mqtt_password") is { } mp) map["Bamf:Mqtt:Password"] = mp;
        }
        // Home Assistant keeps /data for the add-on across updates and restarts.
        if (Environment.GetEnvironmentVariable("BAMF_ADDON_OPTIONS") is not { Length: > 0 }) map["Bamf:DatabasePath"] = "/data/bamf.db";
        return map;
    }
}
