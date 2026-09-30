using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LanWatch.Services;
using static LanWatch.Api.ApiHelpers;

namespace LanWatch.Api;

/// <summary>MQTT, other BAMF servers, the inbound webhooks and Prometheus.</summary>
internal static class IntegrationEndpoints
{
    public static void Map(WebApplication app, string version, Func<HttpContext, bool> HookAllowed)
    {
        app.MapPost("/api/settings/mqtt", (MqttRequest body, HostStore store, MqttPublisher mqtt) =>
        {
            var server = (body.Server ?? "").Trim();
            var port = body.Port ?? 1883;
            if (server.Length > 0 && (server.Contains('/') || server.Contains(' ') || Uri.CheckHostName(server) == UriHostNameType.Unknown))
                return Results.BadRequest(new { error = $"\"{server}\" isn't a server's name or address. Just the name or address, like 192.168.1.20." });
            if (port is < 1 or > 65535) return Results.BadRequest(new { error = "The port is a number from 1 to 65535; 1883 is usual, 8883 with TLS." });
            var prefix = (body.TopicPrefix ?? "bamf").Trim().Trim('/');
            if (!Regex.IsMatch(prefix, "^[A-Za-z0-9_-]+(/[A-Za-z0-9_-]+)*$"))
                return Results.BadRequest(new { error = "The topic prefix is letters, digits, - and _, with / between levels, like bamf." });
            var password = MqttPublisher.KeptPassword(mqtt.Current(), server, port, body.Password);
            store.SetSetting("mqtt", JsonSerializer.Serialize(new MqttPublisher.Saved(server, port, (body.Username ?? "").Trim(), password,
                body.Tls ?? false, body.Discovery ?? true, prefix)));
            mqtt.Reconfigure();
            app.Logger.LogInformation("MQTT set in Settings: {Server}", server.Length > 0 ? $"{server}:{port}" : "off");
            return Results.Json(MqttJson(mqtt));
        });

        app.MapPost("/api/settings/mqtt/reset", (HostStore store, MqttPublisher mqtt) =>
        {
            store.DeleteSetting("mqtt");
            mqtt.Reconfigure();
            return Results.Json(MqttJson(mqtt));
        });

        app.MapPost("/api/settings/remotes", (RemotesRequest body, HostStore store, RemoteService remotes) =>
        {
            var now = remotes.Remotes;
            var list = new List<RemoteService.Remote>();
            foreach (var r in body.Remotes ?? [])
            {
                var name = (r.Name ?? "").Trim();
                var url = (r.Url ?? "").Trim().TrimEnd('/');
                if (name.Length is 0 or > 40) return Results.BadRequest(new { error = "Give each server a name, up to 40 characters." });
                if (list.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return Results.BadRequest(new { error = $"Two servers are called {name}." });
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                    return Results.BadRequest(new { error = $"{name}: the address is where that BAMF opens, like http://10.0.0.5:8840." });
                var password = RemoteService.KeptPassword(now, name, url, r.Password);
                list.Add(new RemoteService.Remote(name, url, string.IsNullOrEmpty(password) ? null : password));
            }
            if (list.Count > 8) return Results.BadRequest(new { error = "Eight servers at most." });
            store.SetSetting("remotes", JsonSerializer.Serialize(list));
            remotes.Reconfigure();
            return Results.Ok();
        });

        app.MapPost("/api/settings/remotes/reset", (HostStore store, RemoteService remotes) =>
        {
            store.DeleteSetting("remotes");
            remotes.Reconfigure();
            return Results.Ok();
        });

        // A new token is shown once, in this answer; after that the dashboard only
        // knows that there is one.
        app.MapPost("/api/settings/hooktoken", (HookTokenRequest body, HostStore store) =>
        {
            switch (body.Action)
            {
                case "generate":
                    var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');
                    store.SetSetting("hookToken", token);
                    return Results.Json(new { token });
                case "reset":
                    store.DeleteSetting("hookToken");
                    return Results.Ok();
                default:
                    return Results.BadRequest(new { error = "Generate or reset." });
            }
        });

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

        // Other BAMF servers being watched, and their devices.
        app.MapGet("/api/remotes", (RemoteService remotes) => Results.Json(new { remotes = remotes.Statuses, hosts = remotes.Hosts() }));
    }
}
