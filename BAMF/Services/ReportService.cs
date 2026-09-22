using System.Text;

namespace LanWatch.Services;

/// <summary>
/// A summary of the network to the webhook, daily, weekly or monthly at an hour you
/// pick: how many devices and how many are up, what's new, what went away,
/// the flakiest device, the longest offline, the least reliable, any watch
/// alerts, and the top talkers. The schedule lives in the settings table so
/// the dashboard can change it; the hour is the server's local time.
/// </summary>
public sealed class ReportService : BackgroundService
{
    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly ILogger<ReportService> _log;

    public ReportService(HostStore store, ScannerService scanner, ILogger<ReportService> log)
    {
        _store = store; _scanner = scanner; _log = log;
    }

    public string Schedule => _store.GetSetting("reportSchedule") is "daily" or "weekly" or "monthly" ? _store.GetSetting("reportSchedule")! : "off";
    public int Hour => int.TryParse(_store.GetSetting("reportHour"), out var h) ? Math.Clamp(h, 0, 23) : 8;
    public int Day => int.TryParse(_store.GetSetting("reportDay"), out var d) ? Math.Clamp(d, 0, 6) : 1;   // Monday
    public DateTime? LastSent => DateTime.TryParse(_store.GetSetting("reportLastSent"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    /// <summary>When the next report is due, in UTC, or null when off.</summary>
    public DateTime? NextDue()
    {
        var schedule = Schedule;
        if (schedule == "off") return null;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.Local);
        var at = new DateTime(local.Year, local.Month, local.Day, Hour, 0, 0, DateTimeKind.Unspecified);
        for (var i = 0; i < 40; i++)
        {
            var candidate = at.AddDays(i);
            if (candidate <= local) continue;
            if (schedule == "weekly" && (int)candidate.DayOfWeek != Day) continue;
            if (schedule == "monthly" && candidate.Day != 1) continue;
            return TimeZoneInfo.ConvertTimeToUtc(candidate, TimeZoneInfo.Local);
        }
        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var schedule = Schedule;
                if (schedule != "off")
                {
                    var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.Local);
                    var due = local.Hour == Hour && (schedule == "daily"
                        || (schedule == "weekly" && (int)local.DayOfWeek == Day)
                        || (schedule == "monthly" && local.Day == 1));
                    var recent = LastSent is { } last && (DateTime.UtcNow - last) <
                        (schedule == "daily" ? TimeSpan.FromHours(20) : schedule == "weekly" ? TimeSpan.FromDays(6) : TimeSpan.FromDays(27));
                    if (due && !recent)
                    {
                        var ok = await SendAsync(schedule, ct);
                        _log.LogInformation("Scheduled {Schedule} report {Result}", schedule, ok ? "sent" : "not sent");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Report check failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>How far back each schedule looks.</summary>
    public static TimeSpan PeriodFor(string schedule) => schedule switch
    {
        "monthly" => TimeSpan.FromDays(30),
        "weekly" => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(1),
    };

    /// <summary>Composes and sends the report for a period; records when, if it went.</summary>
    public async Task<bool> SendAsync(string schedule, CancellationToken ct)
    {
        var (title, text, fields) = Compose(PeriodFor(schedule));
        var ok = await _scanner.SendReport(title, text, fields, ct);
        if (ok) _store.SetSetting("reportLastSent", DateTime.UtcNow.ToString("o"));
        return ok;
    }

    /// <summary>The report as text and as titled fields (for a Discord embed).</summary>
    public (string Title, string Text, List<(string Name, string Value)> Fields) Compose(TimeSpan period)
    {
        var now = DateTime.UtcNow;
        var since = now - period;
        var periodName = period.TotalDays >= 28 ? "the last 30 days" : period.TotalDays >= 6 ? "the last 7 days" : "the last 24 hours";
        var all = _store.GetAll().Where(h => !h.Forgotten && !h.Ignored).ToList();
        string Name(HostRecord h) => h.CustomName != "" ? h.CustomName : h.Hostname != "" ? h.Hostname : h.Ip;
        DateTime When(string iso) => DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : DateTime.MinValue;

        var online = all.Count(h => h.Online);
        var fields = new List<(string, string)>();
        var sb = new StringBuilder();
        var title = $"BAMF report: {online} of {all.Count} devices online";
        sb.AppendLine($"{all.Count} devices, {online} online now, over {periodName}.");
        fields.Add(("Devices", $"{all.Count} known, {online} online ({(all.Count == 0 ? 0 : 100 * online / all.Count)}%)"));

        var fresh = all.Where(h => When(h.FirstSeen) >= since).OrderByDescending(h => When(h.FirstSeen)).ToList();
        var freshText = fresh.Count == 0 ? "None." : string.Join("; ", fresh.Take(8).Select(h => $"{Name(h)} ({h.Ip}{(h.Vendor != "" ? ", " + h.Vendor : "")})")) + (fresh.Count > 8 ? $"; and {fresh.Count - 8} more" : "");
        sb.AppendLine($"New devices: {freshText}");
        fields.Add(($"New devices ({fresh.Count})", freshText));

        var gone = all.Where(h => !h.Online && When(h.LastSeen) >= since).OrderBy(h => When(h.LastSeen)).ToList();
        var goneText = gone.Count == 0 ? "None." : string.Join("; ", gone.Take(8).Select(h => $"{Name(h)} (offline since {Ago(now - When(h.LastSeen))} ago)")) + (gone.Count > 8 ? $"; and {gone.Count - 8} more" : "");
        sb.AppendLine($"Went away: {goneText}");
        fields.Add(($"Went offline and stayed ({gone.Count})", goneText));

        // Addresses that moved and ports that opened, from the same record as Activity's "What changed".
        var changes = _store.ChangesSince(since);
        var byId = all.ToDictionary(h => h.Id);
        if (changes.Moved.Count > 0)
        {
            var t = string.Join("; ", changes.Moved.Where(m => byId.ContainsKey(m.Id)).Take(6).Select(m => $"{Name(byId[m.Id])} {m.Detail}"))
                + (changes.Moved.Count > 6 ? $"; and {changes.Moved.Count - 6} more" : "");
            sb.AppendLine($"Moved address: {t}");
            fields.Add(($"Moved address ({changes.Moved.Count})", t));
        }
        if (changes.Opened.Count > 0)
        {
            var t = string.Join("; ", changes.Opened.Where(p => byId.ContainsKey(p.HostId)).Take(6)
                .Select(p => $"{Name(byId[p.HostId])} {p.Port}{(p.Service != "" ? " (" + p.Service + ")" : "")}"))
                + (changes.Opened.Count > 6 ? $"; and {changes.Opened.Count - 6} more" : "");
            sb.AppendLine($"New open ports: {t}");
            fields.Add(($"New open ports ({changes.Opened.Count})", t));
        }
        var security = changes.Notable.Where(a => a.Kind is "security" or "cert").ToList();
        if (security.Count > 0)
        {
            var t = string.Join("; ", security.Take(5).Select(a => a.Title));
            sb.AppendLine($"Security: {t}");
            fields.Add(($"Security and certificates ({security.Count})", t));
        }

        // Over a month, how the line out of the house did - if it was watched.
        if (period.TotalDays >= 28)
        {
            var outages = _store.GetWanOutageLog(200)
                .Where(o => DateTime.TryParse(o.Start, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var st) && st >= since)
                .ToList();
            if (outages.Count > 0)
            {
                var mins = outages.Sum(o => o.Minutes);
                var worst = outages.OrderByDescending(o => o.Minutes).First();
                var local = outages.Count(o => o.Local);
                var t = $"{outages.Count} outage{(outages.Count == 1 ? "" : "s")}, {mins} minute{(mins == 1 ? "" : "s")} in total; " +
                    $"the longest {worst.Minutes} minute{(worst.Minutes == 1 ? "" : "s")}" +
                    (local > 0 ? $"; {local} with your router down too" : "");
                sb.AppendLine($"Internet: {t}");
                fields.Add(("Internet", t));
            }
            var slow = _store.GetWanSlowLog(200)
                .Where(o => DateTime.TryParse(o.Start, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var st) && st >= since)
                .ToList();
            if (slow.Count > 0)
            {
                var mins = slow.Sum(o => o.Minutes);
                var worst = slow.OrderByDescending(o => o.Minutes).First();
                var t = $"{slow.Count} slow spell{(slow.Count == 1 ? "" : "s")}, {mins} minute{(mins == 1 ? "" : "s")} in total; " +
                    $"the longest {worst.Minutes} minute{(worst.Minutes == 1 ? "" : "s")}, at worst {worst.WorstMs} ms";
                sb.AppendLine($"Slow internet: {t}");
                fields.Add(("Slow internet", t));
            }
        }

        var flaps = _store.OfflineCounts(since);
        var flaky = all.Where(h => flaps.TryGetValue(h.Id, out var n) && n >= 2).OrderByDescending(h => flaps[h.Id]).FirstOrDefault();
        var flakyText = flaky is null ? "Nothing dropped more than once." : $"{Name(flaky)}: offline {flaps[flaky.Id]} times";
        sb.AppendLine($"Flakiest: {flakyText}");
        fields.Add(("Flakiest", flakyText));

        var longest = all.Where(h => !h.Online && h.Known).OrderBy(h => When(h.LastSeen)).FirstOrDefault();
        var longestText = longest is null ? "Every known device is online." : $"{Name(longest)}, offline for {Ago(now - When(longest.LastSeen))}";
        sb.AppendLine($"Longest offline: {longestText}");
        fields.Add(("Longest offline", longestText));

        // Ranked over the report's own span where BAMF keeps one: a month's
        // report by 30-day uptime, anything shorter by the week. A device that
        // was down for a fortnight three weeks ago is the least reliable of the
        // month, even if its last seven days were clean.
        var uptimes = _store.Uptimes();
        var monthly = period.TotalDays >= 28;
        double? Up(long id) => uptimes.TryGetValue(id, out var u) ? (monthly ? u.Month : u.Week) : null;
        var least = all.Where(h => h.Known && Up(h.Id) is { } v && v < 100)
            .OrderBy(h => Up(h.Id)).FirstOrDefault();
        if (least is not null)
        {
            var t = $"{Name(least)} at {Up(least.Id)}% uptime over {(monthly ? "30" : "7")} days";
            sb.AppendLine($"Least reliable: {t}");
            fields.Add(("Least reliable", t));
        }

        var alerts = _scanner.Traffic.Alerts().Where(a => When(a.At) >= since).ToList();
        if (alerts.Count > 0)
        {
            var t = string.Join("; ", alerts.Take(5).Select(a => a.Title));
            sb.AppendLine($"Watch alerts ({alerts.Count}): {t}");
            fields.Add(($"Watch alerts ({alerts.Count})", t));
        }
        {
            var byMac = all.ToDictionary(h => h.Mac, h => h, StringComparer.OrdinalIgnoreCase);
            var top = _store.TrafficTotals(since).Where(kv => byMac.ContainsKey(kv.Key))
                .OrderByDescending(kv => kv.Value.Rx + kv.Value.Tx).Take(3)
                .Select(kv => $"{Name(byMac[kv.Key])} ({Bytes(kv.Value.Rx + kv.Value.Tx)})").ToList();
            if (top.Count > 0)
            {
                sb.AppendLine("Top talkers: " + string.Join("; ", top));
                fields.Add(("Top talkers", string.Join("; ", top)));
            }
        }
        return (title, sb.ToString().TrimEnd(), fields);
    }

    private static string Ago(TimeSpan s)
    {
        if (s.TotalMinutes < 1) return "less than a minute";
        if (s.TotalHours < 1) return $"{(int)s.TotalMinutes} min";
        if (s.TotalDays < 1) return $"{(int)s.TotalHours} h";
        return $"{(int)s.TotalDays} d {s.Hours} h";
    }
    private static string Bytes(long n) => n < 1024 ? $"{n} B" : n < 1048576 ? $"{n / 1024.0:F0} kB" : n < 1073741824 ? $"{n / 1048576.0:F1} MB" : $"{n / 1073741824.0:F2} GB";
}
