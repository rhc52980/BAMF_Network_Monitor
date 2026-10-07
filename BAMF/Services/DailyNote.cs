using System.Text;

namespace LanWatch.Services;

/// <summary>
/// One short message a day, at an hour you pick, saying either that nothing happened or what did. Silence from a
/// monitor means one of two things, and this tells them apart: "All quiet: 23 of 24 devices up, no alerts in the last
/// 24 hours" says BAMF is alive and the network is fine; a day with alerts gets them listed instead, and anything still
/// wrong (a watched device down, the internet down, unusual activity open) is named. Goes to every destination that
/// gets Reports, like the scheduled report. Off by default.
/// </summary>
public sealed class DailyNote : BackgroundService
{
    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly HealthScore _score;
    private readonly WanWatch _wan;
    private readonly ILogger<DailyNote> _log;

    public DailyNote(HostStore store, ScannerService scanner, HealthScore score, WanWatch wan, ILogger<DailyNote> log)
    {
        _store = store; _scanner = scanner; _score = score; _wan = wan; _log = log;
    }

    public bool Enabled => _store.GetSetting("allQuiet") == "true";
    public int Hour => int.TryParse(_store.GetSetting("allQuietHour"), out var h) ? Math.Clamp(h, 0, 23) : 8;
    public DateTime? LastSent => DateTime.TryParse(_store.GetSetting("allQuietLastSent"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
    public DateTime? NextDue() => Enabled ? ReportService.NextDue("daily", Hour, 0, DateTime.UtcNow, TimeZoneInfo.Local) : null;

    /// <summary>The note as a title, a text and fields for an embed; and whether it was in fact all quiet.</summary>
    public (string Title, string Text, List<(string Name, string Value)> Fields, bool Quiet) Compose(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var since = now.AddHours(-24);
        var hosts = _store.GetAll().Where(h => !h.Forgotten && !h.Ignored).ToList();
        var online = hosts.Count(h => h.Online);
        var watched = hosts.Where(h => h.Watched).ToList();
        var watchedDown = watched.Where(h => !h.Online).ToList();
        var alerts = _store.GetAlerts(200, since);
        var unusual = _store.RecentUnusual(24).Where(u => u.ResolvedAt is null).ToList();
        var internetDown = _wan.Enabled && _wan.Current is not null;
        var score = _score.Enabled ? _score.Now() : null;
        string Name(HostRecord h) => h.CustomName != "" ? h.CustomName : h.Hostname is { Length: > 0 } and not "—" ? h.Hostname : h.Ip;

        var fields = new List<(string, string)>();
        var sb = new StringBuilder();
        var quiet = alerts.Count == 0 && watchedDown.Count == 0 && unusual.Count == 0 && !internetDown;
        string title;
        if (quiet)
        {
            title = $"All quiet: {online} of {hosts.Count} devices up";
            sb.Append($"No alerts in the last 24 hours.");
            if (watched.Count > 0) sb.Append($" All {watched.Count} watched device{(watched.Count == 1 ? " is" : "s are")} up.");
            if (_wan.Enabled) sb.Append(" The internet is up.");
            fields.Add(("Devices", $"{online} of {hosts.Count} up{(watched.Count > 0 ? $"; all {watched.Count} watched up" : "")}"));
            fields.Add(("Alerts", "None in the last 24 hours"));
        }
        else
        {
            var wrong = new List<string>();
            if (internetDown) wrong.Add("the internet is down");
            if (watchedDown.Count > 0) wrong.Add(watchedDown.Count == 1 ? $"{Name(watchedDown[0])} is down" : $"{watchedDown.Count} watched devices are down");
            if (unusual.Count > 0) wrong.Add(unusual.Count == 1 ? "something unusual is still going on" : $"{unusual.Count} unusual things are still going on");
            title = alerts.Count > 0
                ? $"Yesterday: {alerts.Count} alert{(alerts.Count == 1 ? "" : "s")}{(wrong.Count > 0 ? ", and " + string.Join(", ", wrong) : "")}"
                : $"Not all quiet: {string.Join(", ", wrong)}";
            sb.Append($"{online} of {hosts.Count} devices up.");
            if (alerts.Count > 0)
            {
                var listed = string.Join("; ", alerts.Take(6).Select(a => a.Title)) + (alerts.Count > 6 ? $"; and {alerts.Count - 6} more" : "");
                sb.Append($" Alerts in the last 24 hours: {listed}.");
                fields.Add(($"Alerts ({alerts.Count})", listed));
            }
            if (wrong.Count > 0)
            {
                var still = string.Join("; ", wrong.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
                sb.Append($" Still: {still}.");
                fields.Add(("Still wrong", still));
            }
            fields.Insert(0, ("Devices", $"{online} of {hosts.Count} up"));
        }
        if (score is not null)
        {
            sb.Append($" Network score {score.Value} ({score.Word.ToLowerInvariant()}).");
            fields.Add(("Network score", $"{score.Value}, {score.Word.ToLowerInvariant()}" + (score.Reasons.Count > 0 ? ": " + string.Join("; ", score.Reasons.Take(3).Select(r => r.Text.ToLowerInvariant())) : "")));
        }
        return (title, sb.ToString(), fields, quiet);
    }

    /// <summary>Composes and sends today's note; records when, if it went.</summary>
    public async Task<bool> SendAsync(CancellationToken ct)
    {
        var (title, text, fields, _) = Compose();
        var ok = await _scanner.SendReport(title, text, fields, ct);
        if (ok) _store.SetSetting("allQuietLastSent", DateTime.UtcNow.ToString("o"));
        return ok;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Enabled && ReportService.IsDue("daily", Hour, 0, DateTime.UtcNow, LastSent, TimeZoneInfo.Local))
                {
                    var ok = await SendAsync(ct);
                    _log.LogInformation("Daily note {Result}", ok ? "sent" : "not sent");
                    // Not sent (no destination takes reports): don't try again sixty times this hour.
                    if (!ok) _store.SetSetting("allQuietLastSent", DateTime.UtcNow.ToString("o"));
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "The daily note failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
