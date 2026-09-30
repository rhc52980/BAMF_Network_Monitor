using System.Text.Json;

namespace LanWatch.Services;

/// <summary>Quiet hours, and the digest of what they held back.</summary>
public partial class ScannerService
{
    // ---------------- quiet hours ----------------

    /// <summary>Quiet hours, local time, from the settings table; empty means none.</summary>
    public (string From, string To) QuietHours => (_store.GetSetting("quietFrom") ?? "", _store.GetSetting("quietTo") ?? "");
    public bool QuietDigest => _store.GetSetting("quietDigest") != "false";

    public bool IsQuietNow()
    {
        var (from, to) = QuietHours;
        if (!TimeOnly.TryParse(from, out var f) || !TimeOnly.TryParse(to, out var t) || f == t) return false;
        var now = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.Local));
        return f <= t ? now >= f && now < t : now >= f || now < t;
    }

    private readonly object _heldLock = new();
    private List<HeldAlert>? _held;
    // Kind is which kind of alert it was, so the digest goes to each destination
    // with just its own kinds. Alerts held before destinations had kinds have
    // none, and go to every destination.
    private sealed record HeldAlert(string At, string Title, string Text, string? Kind = null);
    private List<HeldAlert> Held
    {
        get
        {
            if (_held is null)
            {
                try { _held = JsonSerializer.Deserialize<List<HeldAlert>>(_store.GetSetting("heldAlerts") ?? "[]") ?? new(); }
                catch { _held = new(); }
            }
            return _held;
        }
    }
    public int HeldCount { get { lock (_heldLock) return Held.Count; } }

    /// <summary>Keeps an alert for the digest at the end of quiet hours.</summary>
    private void Hold(string kind, string title, string text)
    {
        lock (_heldLock)
        {
            Held.Add(new HeldAlert(DateTime.UtcNow.ToString("o"), title, text, kind));
            if (Held.Count > 100) Held.RemoveAt(0);
            _store.SetSetting("heldAlerts", JsonSerializer.Serialize(Held));
        }
        _log.LogInformation("Quiet hours: held \"{Title}\"", title);
    }

    /// <summary>
    /// What was held during quiet hours, as one message, once they end: to each
    /// destination, with the held alerts of the kinds it takes.
    /// </summary>
    public async Task<int> FlushHeldAlerts(CancellationToken ct)
    {
        List<HeldAlert> items;
        lock (_heldLock)
        {
            items = Held.ToList();
            Held.Clear();
            _store.SetSetting("heldAlerts", "[]");
        }
        if (items.Count == 0 || !QuietDigest) return items.Count;
        foreach (var d in Destinations())
        {
            var mine = items.Where(i => i.Kind is null || d.Kinds.Contains(i.Kind)).ToList();
            if (mine.Count == 0) continue;
            var fields = mine.Take(15).Select(i => (TimeZoneInfo.ConvertTimeFromUtc(DateTime.Parse(i.At, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal), TimeZoneInfo.Local).ToString("HH:mm") + " " + i.Title, i.Text)).ToList();
            if (mine.Count > 15) fields.Add(("And more", $"{mine.Count - 15} more alert(s) were held."));
            var text = string.Join("\n", mine.Select(i => "- " + i.Title));
            await SendReport($"While it was quiet: {mine.Count} alert{(mine.Count == 1 ? "" : "s")}", text, fields, ct, only: d.Id);
        }
        return items.Count;
    }

    /// <summary>How each kind of alert looks: its icon, colour, footer, ntfy tag and priority.</summary>
    private static (string Icon, int Color, string Footer, string Tag, int Priority) AlertStyle(string kind) => kind switch
    {
        "port" => ("\U0001F513 ", 0xE0A040, "BAMF port watch", "unlock", 4),
        "security" => ("\U0001F6E1 ", 0xE8483B, "BAMF security watch", "shield", 5),
        "internet" => ("\U0001F310 ", 0x4FB3D9, "BAMF internet watch", "globe_with_meridians", 4),
        "cert" => ("\U0001F510 ", 0xE0A040, "BAMF certificate watch", "lock", 4),
        _ => ("⏰ ", 0xB58AF0, "BAMF alert rule", "alarm_clock", 4),
    };

    /// <summary>Which kind of alert, for choosing destinations, each style of alert is.</summary>
    private static string KindOf(string style) => style switch
    {
        "rule" => "status",
        "internet" => "internet",
        _ => "security",   // security, port, cert
    };

    /// <summary>A rule, port, security, certificate or internet alert: title and detail, through the quiet-hours gate.</summary>
    public async Task SendGenericAlert(string title, string detail, string kind, CancellationToken ct)
    {
        var style = AlertStyle(kind);
        var text = $"BAMF: {title}. {detail}";
        HttpRequestMessage Build(string url, string format)
        {
            var payload = format == "discord" ? JsonSerializer.Serialize(new
            {
                username = "BAMF",
                embeds = new[] { new { title = style.Icon + title, description = detail, color = style.Color,
                    timestamp = DateTime.UtcNow.ToString("o"), footer = new { text = style.Footer } } },
            }) : "";
            var generic = JsonSerializer.Serialize(new { content = text, message = text, kind, title, detail });
            return BuildAlertRequest(url, format, title: title, message: detail, priority: style.Priority, tags: style.Tag,
                discordPayload: payload, genericPayload: generic);
        }
        try { await Deliver(KindOf(kind), title, text, Build, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Alert failed"); }
    }

    /// <summary>
    /// A scheduled report: a titled block of text, as an embed with fields on
    /// Discord and as text elsewhere, to every destination that takes reports
    /// (or just the one named). True when one of them accepted it. A report's
    /// hour is the user's own choice, so quiet hours don't hold it.
    /// </summary>
    public async Task<bool> SendReport(string title, string text, IReadOnlyList<(string Name, string Value)> fields, CancellationToken ct, string? only = null)
    {
        HttpRequestMessage Build(string url, string format)
        {
            var payload = format == "discord" ? JsonSerializer.Serialize(new
            {
                username = "BAMF",
                embeds = new[] { new { title, color = 0x4FB3D9, fields = fields.Select(f => new { name = f.Name, value = f.Value.Length > 1000 ? f.Value[..1000] : f.Value, inline = false }).ToArray(),
                    timestamp = DateTime.UtcNow.ToString("o"), footer = new { text = "BAMF scheduled report" } } },
            }) : "";
            var generic = JsonSerializer.Serialize(new { content = title + "\n" + text, message = text, title, fields = fields.Select(f => new { name = f.Name, value = f.Value }) });
            return BuildAlertRequest(url, format, title: title, message: text, priority: 3, tags: "clipboard",
                discordPayload: payload, genericPayload: generic);
        }
        try { return await Deliver("reports", title, text, Build, ct, holdable: false, only: only); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Report failed"); return false; }
    }

    /// <summary>A DHCP or DNS watch alert, to every destination that takes security alerts.</summary>
    private async Task SendWatchAlert(TrafficMonitor.Alert a, CancellationToken ct)
    {
        var text = $"BAMF: {a.Title}. {a.Detail}";
        HttpRequestMessage Build(string url, string format)
        {
            var payload = format == "discord" ? JsonSerializer.Serialize(new
            {
                username = "BAMF",
                embeds = new[] { new { title = "🚨 " + a.Title, description = a.Detail, color = 0xF2716F,
                    timestamp = DateTime.UtcNow.ToString("o"), footer = new { text = "BAMF network watch" } } },
            }) : "";
            var generic = JsonSerializer.Serialize(new { content = text, message = text, kind = a.Kind, title = a.Title, detail = a.Detail });
            return BuildAlertRequest(url, format, title: a.Title, message: a.Detail, priority: 5, tags: "rotating_light",
                discordPayload: payload, genericPayload: generic);
        }
        try { await Deliver("security", a.Title, text, Build, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Watch alert failed"); }
    }

    private static string FormatSpan(TimeSpan s)
    {
        if (s.TotalMinutes < 1) return "less than a minute";
        if (s.TotalHours < 1) return $"{(int)s.TotalMinutes} min";
        if (s.TotalDays < 1) return $"{(int)s.TotalHours} h {s.Minutes} min";
        return $"{(int)s.TotalDays} d {s.Hours} h";
    }
}
