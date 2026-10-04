using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>
/// BAMF's own connections: what it reads from, sends to and calls out to, and whether each is working.
/// For the Network services card on the Activity tab. Only ever status: no address, key, password or
/// webhook is in it, so it's as safe to show to the view-only password as the rest of the dashboard.
/// </summary>
internal static class ConnectionsEndpoints
{
    /// <summary>
    /// One connection. State is "ok", "warn", "error", "waiting" (on, nothing back yet) or "off". Section is
    /// the Settings section that holds it, for a link.
    /// </summary>
    public sealed record Item(string Id, string Group, string Name, string State, string Detail, string? At, string Section);

    public const string Reads = "Reads from", Sends = "Sends to", Internet = "Calls out to the internet";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/connections", (HostStore store, ScannerService scanner, RouterImport import, SwitchCounters switches, RemoteService remotes,
            MqttPublisher mqtt, ReportService reports, NightlyBackup backup, WanWatch wan, SpeedTest speed, GreyNoiseCheck grey, UpdateChecker updates, Heartbeat beat) =>
            Results.Json(new { items = Build(store, scanner, import, switches, remotes, mqtt, reports, backup, wan, speed, grey, updates, beat) }));
    }

    internal static List<Item> Build(HostStore store, ScannerService scanner, RouterImport import, SwitchCounters switches, RemoteService remotes,
        MqttPublisher mqtt, ReportService reports, NightlyBackup backup, WanWatch wan, SpeedTest speed, GreyNoiseCheck grey, UpdateChecker updates, Heartbeat beat)
    {
        var o = new List<Item>();
        static string Iso(DateTime? t) => t?.ToString("o") ?? "";
        static string? At(string? s) => string.IsNullOrEmpty(s) ? null : s;
        static string Plural(int n, string one, string many = "") => n == 1 ? $"1 {one}" : $"{n:N0} {(many == "" ? one + "s" : many)}";

        // ---------- reads from ----------
        var ri = import.Current;
        var routerName = ri.Kind switch { "unifi" => "UniFi controller", "openwrt" => "OpenWrt router", "opnsense" => "OPNsense firewall", "pfsense" => "pfSense firewall", _ => "Router import" };
        if (!ri.Enabled) o.Add(new("router", Reads, "Router import (UniFi, OpenWrt, OPNsense, pfSense)", "off", "Names devices from your router's own list.", null, "network"));
        else
        {
            var traffic = ri.Traffic ? (ri.TrafficError is not null ? $" · traffic: {ri.TrafficError}" : ri.TrafficAt is not null ? $" · counting {Plural(ri.TrafficClients, "device")}" : "") : "";
            o.Add(new("router", Reads, $"{routerName} at {ri.Host}",
                ri.Error is not null ? "error" : ri.LastRun is null ? "waiting" : "ok",
                ri.Error ?? (ri.LastRun is null ? "Not read yet." : $"{Plural(ri.Count, "name")} read{traffic}"), At(ri.LastRun), "network"));
        }

        var sw = switches.Statuses().Where(s => s.Enabled).ToList();
        if (sw.Count == 0) o.Add(new("snmp", Reads, "Managed switches (SNMP)", "off", "Counts each port's traffic, so each device's is known.", null, "network"));
        foreach (var s in sw)
            o.Add(new($"snmp:{s.SwitchId}", Reads, $"Switch {s.Name}", s.Error is not null ? "error" : s.LastPoll is null ? "waiting" : "ok",
                s.Error ?? (s.LastPoll is null ? "Not read yet." : $"{Plural(s.PortsRead, "port")} read, {Plural(s.Counted, "device")} counted"), At(s.LastPoll), "network"));

        var rem = remotes.Statuses;
        if (rem.Count == 0) o.Add(new("remotes", Reads, "Other BAMF servers", "off", "Shows another site's devices here, read-only.", null, "system"));
        foreach (var r in rem)
            o.Add(new($"remote:{r.Name}", Reads, $"BAMF at {r.Name}", r.Ok ? "ok" : r.FetchedUtc is null ? "waiting" : "error",
                r.Ok ? Plural(r.Hosts, "device") : (r.Error ?? "not answering") + (r.FetchedUtc is not null ? ", showing its last answer" : ""), At(r.FetchedUtc), "system"));

        var t = scanner.Traffic;
        if (!scanner.TrafficMonitorEnabled) o.Add(new("traffic", Reads, "Traffic monitor (bandwidth, DHCP and DNS)", "off", "Hears who hands out addresses and answers DNS. Needs Npcap.", null, "scanning"));
        else if (!TrafficMonitor.Available) o.Add(new("traffic", Reads, "Traffic monitor", "error", "The Npcap driver isn't installed.", null, "scanning"));
        else o.Add(new("traffic", Reads, "Traffic monitor", t.Running ? "ok" : "error",
            t.Running ? $"{t.Frames:N0} frames on {string.Join(", ", t.Interfaces)}" : (t.LastError ?? "Not running."), t.Running ? Iso(t.StartedUtc) : null, "scanning"));

        if (!scanner.MdnsEnabled) o.Add(new("mdns", Reads, "mDNS listener", "off", "Hears devices announce their names and services.", null, "scanning"));
        else o.Add(new("mdns", Reads, "mDNS listener", scanner.Mdns.Running ? "ok" : "error",
            scanner.Mdns.Running ? $"{scanner.Mdns.PacketsSeen:N0} packets on {string.Join(", ", scanner.Mdns.Interfaces)}" : (scanner.Mdns.LastError ?? "Not running."), null, "scanning"));

        if (!scanner.Ipv6WatchEnabled) o.Add(new("ipv6", Reads, "IPv6 watch", "off", "Finds devices that only answer over IPv6.", null, "scanning"));
        else o.Add(new("ipv6", Reads, "IPv6 watch", scanner.Ipv6Error is not null ? "error" : scanner.LastIpv6Read is null ? "waiting" : "ok",
            scanner.Ipv6Error ?? (scanner.LastIpv6Read is null ? "Not read yet." : "Reading the neighbour table."), Iso(scanner.LastIpv6Read) is { Length: > 0 } v6 ? v6 : null, "scanning"));

        // ---------- sends to ----------
        var dests = scanner.Destinations();
        if (dests.Count == 0) o.Add(new("webhook", Sends, "Alert webhook (Discord, Slack, ntfy and others)", "off", "Sends alerts somewhere you'll see them.", null, "alerts"));
        foreach (var d in dests)
            o.Add(new($"dest:{d.Id}", Sends, d.Name, d.Kinds.Count == 0 ? "off" : "ok",
                d.Kinds.Count == 0 ? "Takes no kinds of alert." : $"{d.Format} · takes {Plural(d.Kinds.Count, "kind")} of alert. BAMF doesn't keep whether the last one arrived: use Test in Settings.", null, "alerts"));

        if (!mqtt.Configured) o.Add(new("mqtt", Sends, "MQTT (Home Assistant)", "off", "Publishes sensors and device presence to a broker.", null, "system"));
        else o.Add(new("mqtt", Sends, $"MQTT broker at {mqtt.Server}", mqtt.Connected ? "ok" : "error",
            mqtt.Connected ? $"Connected · {Plural((int)Math.Min(int.MaxValue, mqtt.Published), "message")} published" : (mqtt.LastError ?? "Not connected."), Iso(mqtt.LastPublishUtc) is { Length: > 0 } p ? p : null, "system"));

        if (reports.Schedule == "off") o.Add(new("report", Sends, "Scheduled report", "off", "A summary sent on a schedule.", null, "alerts"));
        else o.Add(new("report", Sends, "Scheduled report", reports.LastSent is null ? "waiting" : "ok",
            $"{reports.Schedule}{(reports.NextDue() is { } due ? ", next " + due.ToLocalTime().ToString("ddd d MMM, HH:mm") : "")}", Iso(reports.LastSent) is { Length: > 0 } rs ? rs : null, "alerts"));

        if (!backup.Enabled) o.Add(new("backup", Sends, "Nightly backup", "off", "A copy of BAMF's database, kept for a week.", null, "system"));
        else
        {
            var b = backup.Last;
            var copy = backup.CopyTo is { } c ? (b?.Copied is not null ? " · copied to " + b.Copied : " · second copy goes to " + c) : "";
            o.Add(new("backup", Sends, "Nightly backup", b is null ? "waiting" : b.Error is not null ? "error" : "ok",
                b is null ? "Not run yet." : b.Error ?? $"{b.File} ({FormatBytes(b.Size)}){copy}", b?.At, "system"));
        }

        // ---------- calls out to the internet ----------
        var w = wan.Now();
        if (!w.Enabled) o.Add(new("wan", Internet, "Internet watch", "off", "Pings your router and an address beyond it once a minute.", null, "internet"));
        else o.Add(new("wan", Internet, "Internet watch", w.Up is null ? "waiting" : w.Up == false ? "error" : w.Slow ? "warn" : "ok",
            w.Up is null ? "Waiting for the first ping." : w.Up == false ? (w.RouterUp == true ? $"The internet is down; your router answers." : "The internet is down, and so is your router.")
            : $"{(w.Slow ? "Slow: " : "")}{w.Ms} ms to {w.Target}", At(w.CheckedAt), "internet"));

        var last = store.GetSpeedResults(30).LastOrDefault();
        if (speed.Schedule == "off") o.Add(new("speed", Internet, "Speed test", "off", "Measures the line against Cloudflare, and learns your public address.", null, "internet"));
        else o.Add(new("speed", Internet, $"Speed test ({(speed.Schedule == "daily" ? "daily" : "every 6 hours")})", last is null ? "waiting" : last.Error is not null ? "error" : "ok",
            last is null ? "Not run yet." : last.Error ?? $"{last.DownMbps:0} down, {last.UpMbps:0} up Mbps, {last.PingMs} ms", At(last?.At), "internet"));

        var g = grey.Last;
        if (!grey.Enabled) o.Add(new("greynoise", Internet, "GreyNoise check", "off", "Asks once a day whether your public address has been seen scanning the internet.", null, "security"));
        else o.Add(new("greynoise", Internet, "GreyNoise check", g is null ? "waiting" : g.Error is not null ? "warn" : "ok",
            g is null ? "Not run yet." : g.Error ?? (g.Noise == true ? $"{g.Ip} HAS been seen scanning the internet." : $"{g.Ip} hasn't been seen scanning."), At(g?.CheckedAt), "security"));

        if (!updates.Enabled) o.Add(new("update", Internet, "Update check", "off", "Asks GitHub once a day whether a newer BAMF is out.", null, "system"));
        else o.Add(new("update", Internet, "Update check", updates.LastCheckedUtc is null ? "waiting" : "ok",
            updates.LastCheckedUtc is null ? "Not checked yet." : updates.UpdateAvailable ? $"{updates.LatestVersion} is out." : "Up to date.", Iso(updates.LastCheckedUtc) is { Length: > 0 } uc ? uc : null, "system"));

        var hb = beat.Last;
        if (!beat.Active) o.Add(new("heartbeat", Internet, "Heartbeat", "off", "Visits an address you give it every few minutes, so a monitoring service can tell you when BAMF goes quiet.", null, "system"));
        else o.Add(new("heartbeat", Internet, $"Heartbeat to {Heartbeat.Masked(beat.Url)?.Replace("/…", "")}", hb.Ok is null ? "waiting" : hb.Ok == true ? "ok" : "error",
            hb.Ok is null ? "Not sent yet." : hb.Error ?? $"Every {Plural(beat.Minutes, "minute")}", At(hb.At), "system"));

        return o;
    }

    internal static string FormatBytes(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1e9:0.#} GB",
        >= 1_000_000 => $"{n / 1e6:0.#} MB",
        >= 1_000 => $"{n / 1e3:0.#} KB",
        _ => $"{n} B",
    };
}
