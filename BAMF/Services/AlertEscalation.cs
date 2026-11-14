using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// An alert nobody has acknowledged, sent again somewhere else. Press <b>Acknowledge</b> on an alert in the Alerts card and that is the end of
/// it; leave it, and once it has waited as long as you set (15 minutes to start with) it goes, once, to the destination you chose, say your phone
/// when the first went to a channel nobody was watching. Off by default, and it needs a destination first.
///
/// Only alerts raised after it was switched on count, so turning it on doesn't send yesterday's. Only the groups of alert you ticked are
/// watched (security ones to start with). It stays quiet during quiet hours and a pause, as everything else does, and never sends an alert
/// that wasn't sent in the first place (one for a snoozed device). An alert is escalated at most once, and goes only to its one destination,
/// whatever that destination's own choice of kinds or devices.
/// </summary>
public sealed class AlertEscalation : BackgroundService
{
    public const int DefaultMinutes = 15, MinMinutes = 1, MaxMinutes = 1440;
    /// <summary>How long after an alert was raised it can still be escalated: a missed minute or a quiet-hours gap isn't a reason to send a stale one.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromHours(6);
    /// <summary>The groups of alert that can be escalated, in the order the dashboard lists them.</summary>
    public static readonly string[] Groups = { "security", "status" };
    private const int MostPerRun = 10;

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly ILogger<AlertEscalation> _log;

    public AlertEscalation(HostStore store, ScannerService scanner, ILogger<AlertEscalation> log)
    {
        _store = store; _scanner = scanner; _log = log;
    }

    public bool Enabled => _store.GetSetting("escalate") == "true";
    public int Minutes => int.TryParse(_store.GetSetting("escalateMinutes"), out var m) ? Math.Clamp(m, MinMinutes, MaxMinutes) : DefaultMinutes;
    /// <summary>The destination escalated alerts go to ("main" or an id), or empty.</summary>
    public string To => (_store.GetSetting("escalateTo") ?? "").Trim();

    public ScannerService.Destination? Destination => To == "" ? null : _scanner.Destinations().FirstOrDefault(d => d.Id == To);

    public IReadOnlyList<string> Kinds
    {
        get
        {
            var raw = _store.GetSetting("escalateKinds");
            if (string.IsNullOrEmpty(raw)) return new[] { "security" };
            try { return Clean(JsonSerializer.Deserialize<List<string>>(raw)); }
            catch (JsonException) { return new[] { "security" }; }
        }
    }

    public static List<string> Clean(IEnumerable<string>? kinds) =>
        (kinds ?? Array.Empty<string>()).Select(k => (k ?? "").Trim().ToLowerInvariant()).Where(Groups.Contains).Distinct().ToList();

    /// <summary>When it was switched on, so only alerts after that count.</summary>
    public DateTime? Since => DateTime.TryParse(_store.GetSetting("escalateSince"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime() : null;

    /// <summary>Which group an alert's kind belongs to, or null for one that is never escalated (a wake, which is BAMF doing something, not a warning).</summary>
    public static string? GroupOf(string kind) => kind switch
    {
        "security" or "port" or "cert" or "dhcp" or "dns" or "ra" => "security",
        "rule" or "schedule" or "clock" or "backup" or "ddns" or "internet" => "status",
        _ => null,
    };

    public static string KeyOf(long id) => "a:" + id;
    public static string KeyOf(TrafficMonitor.Alert a) => $"t:{a.At}|{a.Kind}|{a.Title}";

    /// <summary>Every alert BAMF is showing, with its key: the database's and the traffic monitor's.</summary>
    public List<(string Key, string At, string Kind, string Title, string Detail)> Current(int limit = 200)
    {
        var list = _store.GetAlerts(limit).Select(a => (KeyOf(a.Id), a.At, a.Kind, a.Title, a.Detail)).ToList();
        list.AddRange(_scanner.Traffic.Alerts().Select(a => (KeyOf(a), a.At, a.Kind, a.Title, a.Detail)));
        return list;
    }

    /// <summary>How many alerts of the watched groups are waiting for someone: raised since it was switched on, not acknowledged, not escalated yet.</summary>
    public int Waiting()
    {
        if (!Enabled || Since is not { } since) return 0;
        var marks = _store.AlertMarks();
        var kinds = Kinds;
        return Current().Count(a => Eligible(a, since, kinds) && !(marks.TryGetValue(a.Key, out var m) && (m.AckedAt != null || m.EscalatedAt != null)));
    }

    private static bool Eligible((string Key, string At, string Kind, string Title, string Detail) a, DateTime since, IReadOnlyList<string> kinds)
    {
        var group = GroupOf(a.Kind);
        if (group is null || !kinds.Contains(group) || a.Detail.Contains("Not sent")) return false;
        return DateTime.TryParse(a.At, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) && at.ToUniversalTime() > since;
    }

    /// <summary>Sends each alert that has waited long enough and isn't acknowledged. Returns how many it sent.</summary>
    public async Task<int> Run(DateTime nowUtc, CancellationToken ct)
    {
        if (!Enabled || Destination is not { } dest || Since is not { } since || _scanner.IsQuietNow()) return 0;
        var wait = TimeSpan.FromMinutes(Minutes);
        var marks = _store.AlertMarks();
        var kinds = Kinds;
        var sent = 0;
        foreach (var a in Current().OrderBy(a => a.At))
        {
            if (!Eligible(a, since, kinds)) continue;
            if (marks.TryGetValue(a.Key, out var m) && (m.AckedAt != null || m.EscalatedAt != null)) continue;
            var age = nowUtc - DateTime.Parse(a.At, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
            if (age < wait || age > Stale) continue;
            if (sent >= MostPerRun) break;
            _store.MarkAlertEscalated(a.Key);
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.Parse(a.At, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal), TimeZoneInfo.Local);
            await _scanner.SendGenericAlert($"Not acknowledged after {Describe(wait)}: {a.Title}",
                $"{a.Detail} It was raised at {local:HH:mm}, and nobody has pressed Acknowledge on the Alerts card.", "security", ct, only: dest.Id);
            _log.LogInformation("Escalated \"{Title}\" to {Name}", a.Title, dest.Name);
            sent++;
        }
        return sent;
    }

    private static string Describe(TimeSpan t) => t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min" : t.TotalHours % 1 == 0 ? $"{(int)t.TotalHours} h" : $"{(int)t.TotalMinutes} min";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await Run(DateTime.UtcNow, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Alert escalation failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
