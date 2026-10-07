using System.Text;
using System.Text.Json;
using LanWatch.Services;
using static LanWatch.Api.ApiHelpers;

namespace LanWatch.Api;

/// <summary>Settings, backups, the layout file and the wall display.</summary>
internal static class SettingsEndpoints
{
    private static object TidyJson(HostStore store)
    {
        var would = store.StaleDevices(store.TidyDays, DateTime.UtcNow);
        var last = store.LastTidy();
        return new
        {
            enabled = store.TidyEnabled,
            days = store.TidyDays,
            would = would.Count,
            wouldNames = would.Take(20).Select(h => !string.IsNullOrEmpty(h.Hostname) && h.Hostname != "—" ? h.Hostname : h.Ip).ToList(),
            last = last is null ? null : new { at = last.At, count = last.Count, names = last.Names },
        };
    }

    public static void Map(WebApplication app, string version, Func<string?> HookToken, Func<object> HttpsJson)
    {
        // Nightly backups: when, how many to keep, and back up now.
        app.MapPost("/api/settings/backup", (BackupRequest body, HostStore store, NightlyBackup nightly) =>
        {
            if (body.Hour is < 0 or > 23) return Results.BadRequest(new { error = "The hour is 0 to 23." });
            if (body.Keep is < 1 or > 60) return Results.BadRequest(new { error = "Keep 1 to 60 backups." });
            // The second folder: blank for none; otherwise checked, by writing there, before it's saved.
            var copyTo = body.CopyTo?.Trim();
            if (copyTo is { Length: > 0 } && NightlyBackup.CheckCopyTo(copyTo) is { } bad) return Results.BadRequest(new { error = bad });
            if (copyTo is not null) store.SetSetting("backupCopyTo", copyTo);
            if (body.Enabled is { } on) store.SetSetting("backupNightly", on ? "true" : "false");
            if (body.Hour is { } h) store.SetSetting("backupHour", h.ToString());
            if (body.Keep is { } k)
            {
                store.SetSetting("backupKeep", k.ToString());
                if (Directory.Exists(store.BackupsDir)) NightlyBackup.Prune(store.BackupsDir, k);
            }
            return Results.Json(BackupJson(nightly));
        });
        app.MapPost("/api/settings/backup/copyto/reset", (HostStore store, NightlyBackup nightly) =>
        {
            store.DeleteSetting("backupCopyTo");
            return Results.Json(BackupJson(nightly));
        });
        app.MapPost("/api/settings/backup/run", async (NightlyBackup nightly, CancellationToken ct) =>
        {
            var r = await nightly.Run(ct);
            return r.Error is null ? Results.Json(BackupJson(nightly)) : Results.Json(new { error = r.Error }, statusCode: 500);
        });

        // The log of settings changes, newest first.
        app.MapGet("/api/settings/log", (HostStore store) => Results.Json(store.GetSettingsLog(50)));

        // The wall display, /wall: a big-screen status board with no controls, for a
        // TV or a spare tablet. Static, so it sits behind the same Basic auth as the
        // dashboard.
        app.MapGet("/wall", () =>
            Results.File(Path.Combine(app.Environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"), "wall.html"), "text/html"));

        // The recorded layout as one file, and back again. Export names devices by MAC
        // and switches by their place in the file, so it means the same on another server.
        app.MapGet("/api/layout", (HostStore store) =>
        {
            var json = store.ExportLayout(version).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            return Results.Text(json, "application/json", System.Text.Encoding.UTF8);
        });

        // The settings that make BAMF behave the way you've set it, without anything secret and without what belongs
        // to this site, for setting up another BAMF the same way.
        app.MapGet("/api/settings/export", (HostStore store) =>
        {
            var json = store.ExportSettings(version).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            return Results.Text(json, "application/json", System.Text.Encoding.UTF8);
        });

        app.MapPost("/api/settings/import", (System.Text.Json.JsonElement body, HostStore store, RemoteService remotes) =>
        {
            var (result, error) = store.ImportSettings(body);
            if (result is null) return Results.BadRequest(new { error });
            if (result.RemotesChanged) remotes.Reconfigure();
            return Results.Json(new { applied = result.Applied, rules = result.RulesApplied, rulesSkipped = result.RulesSkipped, remotes = result.Remotes, ignored = result.Ignored });
        });

        app.MapPost("/api/layout", (System.Text.Json.JsonElement body, HostStore store) =>
        {
            var r = store.ImportLayout(body);
            return r.Error is null ? Results.Json(new { r.Devices, r.Switches, r.Skipped }) : Results.BadRequest(new { error = r.Error });
        });

        // The whole database as one file, for a backup kept somewhere else. The copy
        // goes to a temp file and is deleted as soon as it has been sent.
        app.MapGet("/api/backup", (HostStore store) =>
        {
            var file = store.SnapshotTo(Path.Combine(Path.GetTempPath(), "bamf-backup"));
            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose);
            return Results.File(stream, "application/vnd.sqlite3", $"bamf-{DateTime.Now:yyyyMMdd-HHmm}.db");
        });

        // Forgetting devices that have been gone a long time and that nobody has taken any notice of. The automatic
        // daily run is switched on here; the button works either way.
        app.MapGet("/api/settings/tidy", (HostStore store) => Results.Json(TidyJson(store)));

        app.MapPost("/api/settings/tidy", (TidyRequest body, HostStore store) =>
        {
            var days = body.Days ?? store.TidyDays;
            if (days is < HostStore.MinTidyDays or > HostStore.MaxTidyDays) return Results.BadRequest(new { error = $"Between {HostStore.MinTidyDays} and {HostStore.MaxTidyDays} days." });
            store.SetSetting("tidyDays", days.ToString());
            store.SetSetting("tidyEnabled", body.Enabled ? "true" : "false");
            return Results.Json(TidyJson(store));
        });

        app.MapPost("/api/tidy/run", (HostStore store) =>
        {
            var r = store.TidyStale(store.TidyDays, DateTime.UtcNow);
            return Results.Json(new { count = r.Count, names = r.Names });
        });

        app.MapPost("/api/settings/newdays", (NewDaysRequest body, HostStore store) =>
        {
            if (body.Days is < 1 or > 90) return Results.BadRequest(new { error = "Between 1 and 90 days." });
            store.SetSetting("newDays", body.Days.ToString());
            return Results.Json(new { days = body.Days });
        });

        // Puts a backup back. The upload goes to a temp file, is checked, and only
        // then replaces the database; the one it replaces is kept in backups first.
        // A database can be bigger than the 30 MB Kestrel allows a request by
        // default, so this one route is allowed up to a gigabyte.
        app.MapPost("/api/backup/restore", async (HttpRequest req, HostStore store, CancellationToken ct) =>
        {
            var size = req.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false }) size.MaxRequestBodySize = 1L << 30;
            var dir = Path.Combine(Path.GetTempPath(), "bamf-backup");
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, $"restore-{Guid.NewGuid():N}.db");
            try
            {
                await using (var fs = File.Create(tmp)) await req.Body.CopyToAsync(fs, ct);
                var (kept, error) = store.RestoreFrom(tmp);
                return error is null ? Results.Json(new { ok = true, kept }) : Results.BadRequest(new { error });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                app.Logger.LogWarning(ex, "Restoring a backup failed");
                return Results.Json(new { error = $"Couldn't restore it: {ex.Message} The database is as it was." }, statusCode: 500);
            }
            finally { try { File.Delete(tmp); } catch { } }
        });

        // The backups kept on this machine, and putting one of them back.
        app.MapGet("/api/backup/saved", (HostStore store) => Results.Json(store.SavedBackups()));
        app.MapPost("/api/backup/saved/restore", (SavedRestoreRequest body, HostStore store) =>
        {
            if (store.SavedBackupPath(body.Name) is not { } path) return Results.NotFound(new { error = "There's no backup by that name here." });
            try
            {
                var (kept, error) = store.RestoreFrom(path);
                return error is null ? Results.Json(new { ok = true, kept }) : Results.BadRequest(new { error });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                app.Logger.LogWarning(ex, "Restoring a saved backup failed");
                return Results.Json(new { error = $"Couldn't restore it: {ex.Message} The database is as it was." }, statusCode: 500);
            }
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

        // A watched device slow to answer: the switch, and the ms it has to reach.
        app.MapPost("/api/settings/latency-alert", (LatencyAlertRequest body, HostStore store, ScannerService scanner) =>
        {
            if (body.Ms is { } ms && ms is < 20 or > 5000) return Results.BadRequest(new { error = "Between 20 and 5000 ms." });
            if (body.Enabled is { } on) store.SetSetting("latencyAlert", on ? "true" : "false");
            if (body.Ms is { } v) store.SetSetting("latencyAlertMs", v.ToString());
            return Results.Json(new { enabled = scanner.LatencyAlertEnabled, ms = scanner.LatencyAlertMs });
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

        // Everything the Settings tab renders, in one round trip. Split into what the
        // dashboard may change and what it may only display: anything that needs a
        // restart to apply stays in appsettings.json. The networks can be changed here,
        // but only to private ones no bigger than a sweep covers (see
        // ScannerService.CheckNetwork): the wildcard port-scan guard expands a pattern
        // across the networks, so this is what keeps it on private addresses.
        app.MapGet("/api/settings", (HostStore store, ScannerService scanner, UpdateChecker updates, IConfiguration cfg, ReportService reports, MqttPublisher mqtt, RuleService rulesSvc, RemoteService remotesSvc2, WanWatch wan) =>
        {
            var overrides = scanner.ReadIntervalOverrides();
            return Results.Ok(new
            {
                editable = new
                {
                    https = HttpsJson(),
                    backup = BackupJson(app.Services.GetRequiredService<NightlyBackup>()),
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
                    latencyAlert = scanner.LatencyAlertEnabled,
                    latencyAlertMs = scanner.LatencyAlertMs,
                    dnsWatch = store.GetSetting("dnsWatch") == "true",
                    wanQuality = store.GetSetting("wanQuality") != "false",
                    arpWatch = scanner.ArpWatchEnabled,
                    certWatch = store.GetSetting("certWatch") != "false",
                    flowWatch = store.GetSetting("flowWatch") != "false",
                    spikeAlert = store.GetSetting("spikeAlert") != "false",
                    firstWeekReport = store.GetSetting("firstWeekReport") != "false",
                    ipv6Watch = scanner.Ipv6WatchEnabled,
                    greynoise = store.GetSetting("greynoise") == "true",
                    wanWatch = wan.Enabled,
                    wanTarget = wan.Target,
                    wanInterval = wan.IntervalSeconds,
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
                    webhookKinds = scanner.MainKinds,
                    destinations = DestinationsJson(scanner),
                    networks = NetworksJson(scanner),
                    mqtt = MqttJson(mqtt),
                    remotes = new
                    {
                        source = remotesSvc2.Source,
                        list = remotesSvc2.Remotes.Select(r => new { name = r.Name, url = r.Url, password = !string.IsNullOrEmpty(r.Password) }),
                    },
                    hookToken = new
                    {
                        source = store.GetSetting("hookToken") is { Length: > 0 } ? "settings" : cfg["Bamf:HookToken"] is { Length: > 0 } ? "file" : null,
                    },
                },
                readOnly = new
                {
                    subnets = scanner.SubnetLabels,
                    urls = cfg["Urls"] ?? "",
                    databasePath = cfg["Bamf:DatabasePath"] ?? "bamf.db",
                    autoDownloadOui = cfg.GetValue("Bamf:AutoDownloadOui", true),
                    updateRepo = cfg["Bamf:UpdateRepo"] ?? "",
                    hookToken = HookToken() is not null,
                    viewerPassword = app.Services.GetRequiredService<AuthService>().Source("viewer") is not null,
                    remotes = remotesSvc2.Statuses,
                    mqtt = new
                    {
                        configured = mqtt.Configured, server = mqtt.Configured ? mqtt.Server : null, connected = mqtt.Connected,
                        error = mqtt.LastError, published = mqtt.Published, lastPublish = mqtt.LastPublishUtc?.ToString("o"),
                        discovery = mqtt.Current()?.Discovery ?? true, topicPrefix = mqtt.Current()?.TopicPrefix ?? "bamf",
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

            // Pausing only ever narrows: a label must already be one of the networks.
            if (body.DisabledSubnets is { } paused)
            {
                var configured = new HashSet<string>(scanner.CurrentNetworks(), StringComparer.OrdinalIgnoreCase);
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

        // Drops every dashboard-saved scan setting, the networks included, so
        // appsettings.json is authoritative again. Deliberately does not touch the webhook or the toggles that predate the
        // Settings tab - those have their own controls and their own meaning of "off".
        app.MapPost("/api/settings/scan/reset", (HostStore store) =>
        {
            foreach (var key in new[]
                     {
                         "scanIntervalSeconds", "pingConcurrency",
                         "historyRetentionDays", "subnetScanIntervalSeconds", "disabledSubnets",
                         "offlineAfterMissedScans", "mdnsListen", "subnets",
                     })
                store.DeleteSetting(key);
            return Results.Ok();
        });
    }
}
