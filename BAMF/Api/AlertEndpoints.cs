using LanWatch.Services;
using static LanWatch.Api.ApiHelpers;

namespace LanWatch.Api;

/// <summary>Where alerts go, alert rules, quiet hours and the scheduled report.</summary>
internal static class AlertEndpoints
{
    public static void Map(WebApplication app)
    {
        // Alerts BAMF raised: rules, ports, DHCP and DNS, newest first.
        app.MapGet("/api/alerts", (HostStore store, ScannerService scanner) =>
        {
            var mine = store.GetAlerts(50).Select(a => new { at = a.At, kind = a.Kind, title = a.Title, detail = a.Detail });
            var watch = scanner.Traffic.Alerts().Select(a => new { at = a.At, kind = a.Kind, title = a.Title, detail = a.Detail });
            return Results.Json(mine.Concat(watch).OrderByDescending(a => a.at).Take(50));
        });

        // BAMF's own warnings and errors since it started, for the Problems card.
        app.MapGet("/api/problems", (ProblemLog problems) => Results.Json(new
        {
            since = problems.StartedUtc.ToString("o"),
            rows = problems.Rows().Select(r => new { source = r.Source, level = r.Level, message = r.Message, count = r.Count, first = r.FirstUtc.ToString("o"), last = r.LastUtc.ToString("o") }),
        }));
        app.MapPost("/api/problems/clear", (ProblemLog problems) => { problems.Clear(); return Results.Ok(); });

        // Alert rules, and quiet hours.
        app.MapGet("/api/settings/rules", (RuleService rules, ScannerService scanner) => Results.Json(RulesJson(rules, scanner)));

        app.MapPost("/api/settings/rules", (List<RuleService.Rule> body, RuleService rules, ScannerService scanner) =>
        {
            var error = rules.SaveRules(body ?? new());
            return error is null ? Results.Json(RulesJson(rules, scanner)) : Results.BadRequest(new { error });
        });

        app.MapPost("/api/settings/quiet", (QuietRequest body, HostStore store, RuleService rules, ScannerService scanner) =>
        {
            var from = (body.From ?? "").Trim(); var to = (body.To ?? "").Trim();
            if ((from != "" || to != "") && (!TimeOnly.TryParse(from, out _) || !TimeOnly.TryParse(to, out _)))
                return Results.BadRequest(new { error = "Give quiet hours a from and to time, like 23:00 and 07:00." });
            store.SetSetting("quietFrom", from); store.SetSetting("quietTo", to);
            store.SetSetting("quietDigest", body.Digest ? "true" : "false");
            return Results.Json(RulesJson(rules, scanner));
        });

        // Pause every alert for a while, from the Tools menu. 0 minutes ends it now.
        app.MapPost("/api/alerts/pause", async (PauseRequest body, ScannerService scanner, CancellationToken ct) =>
        {
            if (!scanner.PauseEnabled) return Results.Conflict(new { error = "Pausing alerts is switched off in Settings." });
            if (body.Minutes > ScannerService.MaxPauseMinutes) return Results.BadRequest(new { error = "Three days at most." });
            var until = await scanner.PauseAlerts(body.Minutes, ct);
            return Results.Json(new { pausedUntil = until?.ToString("o") });
        });

        // Whether the Pause alerts control is offered at all. Switching it off ends a pause that's running.
        app.MapPost("/api/settings/pause", async (ActiveArpRequest body, HostStore store, ScannerService scanner, CancellationToken ct) =>
        {
            store.SetSetting("pauseAlerts", body.Enabled ? "true" : "false");
            if (!body.Enabled && !scanner.IsQuietNow()) await scanner.FlushHeldAlerts(ct);
            return Results.Json(new { enabled = scanner.PauseEnabled });
        });

        app.MapPost("/api/settings/port-watch", (ActiveArpRequest body, HostStore store) =>
        {
            store.SetSetting("portWatch", body.Enabled ? "true" : "false");
            return Results.Ok();
        });

        // The alerts-off banner's "Don't remind me". Saved on the server, so it holds
        // in every browser rather than coming back on the next device.
        app.MapPost("/api/settings/alertnudge", (NudgeRequest body, HostStore store) =>
        {
            store.SetSetting("alertsNudgeOff", body.Off ? "true" : "");
            return Results.Json(new { off = body.Off });
        });

        // Scheduled reports: off, daily, weekly or monthly at an hour of the server's local day.
        app.MapPost("/api/settings/report", (ReportRequest body, HostStore store, ReportService reports) =>
        {
            var schedule = (body.Schedule ?? "off").ToLowerInvariant();
            if (schedule is not ("off" or "daily" or "weekly" or "monthly")) return Results.BadRequest(new { error = "Schedule is off, daily, weekly or monthly." });
            store.SetSetting("reportSchedule", schedule);
            store.SetSetting("reportHour", Math.Clamp(body.Hour ?? 8, 0, 23).ToString());
            store.SetSetting("reportDay", Math.Clamp(body.Day ?? 1, 0, 6).ToString());
            return Results.Json(ReportJson(reports));
        });

        // Sends the report now, whatever the schedule, and returns what was sent.
        app.MapPost("/api/reports/send", async (ReportService reports, ScannerService scanner, CancellationToken ct) =>
        {
            if (!scanner.Takes("reports")) return Results.BadRequest(new { error = scanner.AnyDestination
                ? "No destination is set to get reports. Tick Reports on one above."
                : "No webhook saved. Add one above first." });
            var schedule = reports.Schedule is "weekly" or "monthly" ? reports.Schedule : "daily";
            var (title, text, _) = reports.Compose(ReportService.PeriodFor(schedule));
            var ok = await reports.SendAsync(schedule, ct);
            return ok ? Results.Json(new { ok = true, title, text }) : Results.Json(new { ok = false, error = "The webhook endpoint didn't accept it.", title, text });
        });

        // The report as it would be sent, for a look before turning it on.
        app.MapGet("/api/reports/preview", (ReportService reports) =>
        {
            var (title, text, _) = reports.Compose(ReportService.PeriodFor(reports.Schedule));
            return Results.Json(new { title, text });
        });

        // Save or clear the webhook endpoint from the dashboard, so it doesn't need a
        // config edit and a service restart. Stored in the database, which overrides
        // Bamf:WebhookUrl exactly like the other runtime settings.
        app.MapPost("/api/settings/webhook", (WebhookRequest body, HostStore store, ScannerService scanner) =>
        {
            var url = (body.Url ?? "").Trim();

            // Delivery format travels with the URL. Anything unrecognised means "auto",
            // which is the original behaviour: Discord embed for a Discord URL, generic
            // JSON for everything else.
            var format = (body.Format ?? "auto").Trim().ToLowerInvariant();
            if (format is not ("auto" or "ntfy" or "gotify" or "json" or "discord")) format = "auto";
            store.SetSetting("webhookFormat", format);

            if (url.Length == 0)
            {
                store.SetSetting("webhookUrl", "");
                store.SetSetting("alertsNudgeOff", "");
                return Results.Json(new { ok = true, configured = false, masked = (string?)null, format });
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
                format,
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

        // Which kinds of alert the main webhook gets: devices, status, security,
        // internet, reports. Every kind unless some are unticked.
        app.MapPost("/api/settings/webhookkinds", (KindsRequest body, ScannerService scanner) =>
        {
            scanner.SetMainKinds(body.Kinds ?? new());
            return Results.Json(new { kinds = scanner.MainKinds });
        });

        // The destinations besides the main webhook, replaced as a list. A saved one
        // sent back with a blank URL keeps the URL it has.
        app.MapPost("/api/settings/destinations", (DestinationsRequest body, ScannerService scanner) =>
        {
            var error = scanner.SaveExtraDestinations(body.Destinations ?? new());
            return error is null ? Results.Json(new { destinations = DestinationsJson(scanner) }) : Results.BadRequest(new { error });
        });

        // A test to one destination: "main" for the main webhook, or an extra's id.
        app.MapPost("/api/destinations/{id}/test", async (string id, ScannerService scanner, CancellationToken ct) =>
        {
            var error = await scanner.SendTestNotification(ct, id);
            return error is null ? Results.Ok(new { ok = true }) : Results.Json(new { ok = false, error });
        });
    }
}
