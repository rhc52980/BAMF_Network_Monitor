using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// Alert rules, quiet hours and the daily port watch, checked once a minute.
///
/// A rule names a target (every device, the watched ones, a tag, or one
/// device) and one of three conditions: offline for longer than N minutes,
/// back online, or online during a window of the day ("kids' devices after
/// 10 pm"). Each fires once per occurrence: once per outage, once per return,
/// once per device per day for the window.
///
/// Quiet hours are enforced where alerts are sent (see ScannerService): held
/// alerts are delivered as one digest when the quiet ends, from here.
///
/// The port watch scans every online known device's common ports once a day,
/// so a port that opens later is noticed without anyone running a scan.
/// </summary>
public sealed class RuleService : BackgroundService
{
    public sealed record Rule(string Id, string Name, string Kind, string Target, int Minutes, string From, string To, bool Enabled);

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly SecurityCheck _security;
    private readonly GreyNoiseCheck _greynoise;
    private DateTime _lastSecurityUtc = DateTime.MinValue;
    private readonly ILogger<RuleService> _log;
    private readonly Dictionary<string, string> _fired = new();   // rule:host -> what it fired for
    private readonly Dictionary<string, bool> _online = new();    // rule:host -> online at the last check
    private bool _wasQuiet;
    private DateTime _lastPortWatchUtc = DateTime.MinValue;

    public RuleService(HostStore store, ScannerService scanner, SecurityCheck security, GreyNoiseCheck greynoise, ILogger<RuleService> log)
    {
        _store = store; _scanner = scanner; _security = security; _greynoise = greynoise; _log = log;
        try { _fired = JsonSerializer.Deserialize<Dictionary<string, string>>(_store.GetSetting("ruleFired") ?? "{}") ?? new(); } catch { }
        _wasQuiet = _scanner.IsQuietNow();
    }

    public List<Rule> Rules
    {
        get { try { return JsonSerializer.Deserialize<List<Rule>>(_store.GetSetting("alertRules") ?? "[]") ?? new(); } catch { return new(); } }
    }

    /// <summary>Replaces the rules. Returns an error for the user, or null.</summary>
    public string? SaveRules(IEnumerable<Rule> rules)
    {
        var clean = new List<Rule>();
        foreach (var r in rules)
        {
            var kind = (r.Kind ?? "").ToLowerInvariant();
            if (kind is not ("offline" or "online" or "hours" or "wake")) return "Each rule is offline, online, hours or wake.";
            var target = (r.Target ?? "any").Trim();
            if (!(target is "any" or "watched" || target.StartsWith("tag:") || target.StartsWith("host:"))) return "Pick what the rule watches.";
            if (kind == "hours" && (!TimeOnly.TryParse(r.From ?? "", out _) || !TimeOnly.TryParse(r.To ?? "", out _))) return "Give the hours rule a from and to time.";
            if (kind == "wake")
            {
                if (!TimeOnly.TryParse(r.From ?? "", out _)) return "Give the wake rule a time.";
                if (target == "any") return "Wake one device, a tag or the watched devices, not every device.";
                if (!WakeDays.ContainsKey((r.To ?? "").Trim().ToLowerInvariant() is { Length: > 0 } d ? d : "daily")) return "Wake daily, on weekdays or at weekends.";
            }
            var name = (r.Name ?? "").Trim();
            if (name.Length > 60) name = name[..60];
            clean.Add(new Rule(string.IsNullOrWhiteSpace(r.Id) ? Guid.NewGuid().ToString("N")[..8] : r.Id, name, kind, target,
                Math.Clamp(r.Minutes, 0, 7 * 24 * 60), r.From ?? "",
                kind == "wake" ? ((r.To ?? "").Trim().ToLowerInvariant() is { Length: > 0 } days ? days : "daily") : r.To ?? "", r.Enabled));
        }
        if (clean.Count > 50) return "At most 50 rules.";
        _store.SetSetting("alertRules", JsonSerializer.Serialize(clean));
        return null;
    }

    public bool PortWatchEnabled => _store.GetSetting("portWatch") == "true";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Tick(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Rule check failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task Tick(CancellationToken ct)
    {
        // Quiet hours just ended: whatever was held goes out as one message.
        var quiet = _scanner.IsQuietNow();
        if (_wasQuiet && !quiet) await _scanner.FlushHeldAlerts(ct);
        _wasQuiet = quiet;

        var rules = Rules.Where(r => r.Enabled).ToList();
        if (rules.Count > 0)
        {
            var hosts = _store.GetAll().Where(h => !h.Ignored && !h.Forgotten).ToList();
            var tags = _store.GetTags();
            var now = DateTime.UtcNow;
            var local = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.Local);
            var changed = false;
            foreach (var rule in rules)
            {
                foreach (var h in hosts.Where(h => Matches(rule.Target, h, tags)))
                {
                    var key = rule.Id + ":" + h.Id;
                    var name = NameOf(h);
                    switch (rule.Kind)
                    {
                        case "offline":
                        {
                            if (h.Online) break;
                            if (!DateTime.TryParse(h.LastSeen, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var last)) break;
                            if ((now - last).TotalMinutes < rule.Minutes) break;
                            if (_fired.TryGetValue(key, out var f) && f == h.LastSeen) break;   // this outage already alerted
                            _fired[key] = h.LastSeen; changed = true;
                            await Fire(rule, $"{name} has been offline for {Ago(now - last)}", $"{name} ({h.Ip}) was last seen {Ago(now - last)} ago. Rule: {Describe(rule)}.", ct);
                            break;
                        }
                        case "online":
                        {
                            var was = _online.TryGetValue(key, out var w) ? w : (bool?)null;
                            _online[key] = h.Online;
                            if (was == false && h.Online)
                                await Fire(rule, $"{name} is back online", $"{name} ({h.Ip}) is online again. Rule: {Describe(rule)}.", ct);
                            break;
                        }
                        case "wake":
                        {
                            // Once, at the set time, on the set days. A device that is
                            // already up is left alone, but still counts as done today.
                            if (!TimeOnly.TryParse(rule.From, out var at) || !WakeDays.TryGetValue(rule.To, out var days) || !days.Contains(local.DayOfWeek)) break;
                            var since = (TimeOnly.FromDateTime(local) - at).TotalMinutes;
                            if (since < 0 || since >= 10) break;
                            var today = local.ToString("yyyy-MM-dd");
                            if (_fired.TryGetValue(key, out var f) && f == today) break;
                            _fired[key] = today; changed = true;
                            if (h.Online) break;
                            var ok = await WakeOnLan.WakeHostAsync(h);
                            _log.LogInformation("Scheduled wake of {Name} ({Mac}): {Result}", name, h.Mac, ok ? "sent" : "failed");
                            _store.AddAlert("wake", ok ? $"Woke {name} at {local:HH:mm}" : $"Couldn't wake {name}",
                                ok ? $"Sent a Wake-on-LAN packet to {name} ({h.Mac}). Rule: {Describe(rule)}." : $"The Wake-on-LAN packet to {name} ({h.Mac}) couldn't be sent. Rule: {Describe(rule)}.");
                            break;
                        }
                        case "hours":
                        {
                            if (!h.Online || !InWindow(local, rule.From, rule.To)) break;
                            var day = local.ToString("yyyy-MM-dd");
                            if (_fired.TryGetValue(key, out var f) && f == day) break;
                            _fired[key] = day; changed = true;
                            await Fire(rule, $"{name} is online at {local:HH:mm}", $"{name} ({h.Ip}) is online during {rule.From}–{rule.To}. Rule: {Describe(rule)}.", ct);
                            break;
                        }
                    }
                }
            }
            if (changed)
            {
                // Keep the memory of what fired from growing without end.
                foreach (var k in _fired.Keys.Where(k => !rules.Any(r => k.StartsWith(r.Id + ":"))).ToList()) _fired.Remove(k);
                _store.SetSetting("ruleFired", JsonSerializer.Serialize(_fired));
            }
        }

        // The daily port watch, at four in the morning local time.
        if (PortWatchEnabled)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.Local);
            if (local.Hour == 4 && (DateTime.UtcNow - _lastPortWatchUtc).TotalHours > 20)
            {
                _lastPortWatchUtc = DateTime.UtcNow;
                await PortWatch(ct);
            }
        }

        // The certificate watch at half past four, after the port watch has
        // found this morning's HTTPS ports. With the port watch on, the UPnP
        // search goes along: both are active checks the user switched on.
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.Local);
            if (local.Hour == 4 && local.Minute >= 30 && (DateTime.UtcNow - _lastSecurityUtc).TotalHours > 20
                && (_security.CertWatchEnabled || PortWatchEnabled))
            {
                _lastSecurityUtc = DateTime.UtcNow;
                await _security.Run(null, _security.CertWatchEnabled, PortWatchEnabled, ct);
            }
        }

        // GreyNoise, once a day, only if it was switched on.
        if (_greynoise.Due) await _greynoise.Check(ct);
    }

    private static bool Matches(string target, HostRecord h, Dictionary<long, List<string>> tags)
    {
        if (target == "any") return true;
        if (target == "watched") return h.Watched;
        if (target.StartsWith("tag:")) return tags.TryGetValue(h.Id, out var t) && t.Any(x => string.Equals(x, target[4..], StringComparison.OrdinalIgnoreCase));
        if (target.StartsWith("host:") && long.TryParse(target[5..], out var id)) return h.Id == id;
        return false;
    }

    /// <summary>A device's name for an alert: its own, its hostname, the router's name for it, or its address.</summary>
    private string NameOf(HostRecord h)
    {
        if (h.CustomName != "") return h.CustomName;
        if (h.Hostname != "") return h.Hostname;
        return _store.GetRouterNames().TryGetValue(h.Mac, out var n) && n != "" ? n : h.Ip;
    }

    /// <summary>The days a wake rule runs on.</summary>
    public static readonly Dictionary<string, DayOfWeek[]> WakeDays = new()
    {
        ["daily"] = Enum.GetValues<DayOfWeek>(),
        ["weekdays"] = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
        ["weekends"] = new[] { DayOfWeek.Saturday, DayOfWeek.Sunday },
    };

    private static bool InWindow(DateTime local, string from, string to)
    {
        if (!TimeOnly.TryParse(from, out var f) || !TimeOnly.TryParse(to, out var t)) return false;
        var now = TimeOnly.FromDateTime(local);
        return f <= t ? now >= f && now < t : now >= f || now < t;   // a window across midnight
    }

    public string Describe(Rule r)
    {
        var who = r.Target == "any" ? "any device" : r.Target == "watched" ? "watched devices"
            : r.Target.StartsWith("tag:") ? $"devices tagged {r.Target[4..]}"
            : r.Target.StartsWith("host:") && long.TryParse(r.Target[5..], out var id) && _store.GetAll().FirstOrDefault(h => h.Id == id) is { } h
                ? NameOf(h) : "one device";
        return r.Kind switch
        {
            "offline" => $"{who} offline for more than {r.Minutes} min",
            "online" => $"{who} back online",
            "wake" => $"wake {who} at {r.From}{(r.To == "weekdays" ? " on weekdays" : r.To == "weekends" ? " at weekends" : " every day")}",
            _ => $"{who} online between {r.From} and {r.To}",
        };
    }

    private async Task Fire(Rule rule, string title, string detail, CancellationToken ct)
    {
        _log.LogWarning("Rule {Name}: {Title}", rule.Name == "" ? rule.Id : rule.Name, title);
        _store.AddAlert("rule", title, detail);
        await _scanner.SendGenericAlert(title, detail, "rule", ct);
    }

    /// <summary>Scans every online known device's common ports, a few at a time, and alerts on new open ones.</summary>
    public async Task<int> PortWatch(CancellationToken ct)
    {
        var hosts = _store.GetAll().Where(h => h.Online && h.Known && !h.Ignored && !h.Forgotten).ToList();
        var found = 0;
        foreach (var h in hosts)
        {
            ct.ThrowIfCancellationRequested();
            List<PortChecker.PortInfo> open;
            try { open = await PortChecker.ScanAsync(h.Ip, 700, ct, 8); }
            catch (OperationCanceledException) { throw; }
            catch { continue; }
            found += await RecordScan(h, PortChecker.CommonPorts.Select(p => p.Port), open, ct);
        }
        _log.LogInformation("Port watch scanned {Count} device(s)", hosts.Count);
        return found;
    }

    /// <summary>Records a scan's result and alerts on ports that opened since the last one. Returns how many.</summary>
    public async Task<int> RecordScan(HostRecord h, IEnumerable<int> scanned, IEnumerable<PortChecker.PortInfo> open, CancellationToken ct)
    {
        var (newly, closed, first) = _store.RecordPortScan(h.Id, scanned, open.Select(o => (o.Port, o.Service)));
        if (first || newly.Count == 0) return 0;
        var name = NameOf(h);
        var list = string.Join(", ", newly.Select(p => p.Service != "" ? $"{p.Port} ({p.Service})" : p.Port.ToString()));
        var title = $"New open port{(newly.Count == 1 ? "" : "s")} on {name}: {list}";
        var detail = $"{name} ({h.Ip}) is now listening on {list}, which it wasn't the last time BAMF scanned it." +
            (closed.Count > 0 ? $" Closed since then: {string.Join(", ", closed)}." : "");
        _store.AddAlert("port", title, detail);
        await _scanner.SendGenericAlert(title, detail, "port", ct);
        return newly.Count;
    }

    private static string Ago(TimeSpan s)
    {
        if (s.TotalMinutes < 1) return "less than a minute";
        if (s.TotalHours < 1) return $"{(int)s.TotalMinutes} min";
        if (s.TotalDays < 1) return $"{(int)s.TotalHours} h {s.Minutes} min";
        return $"{(int)s.TotalDays} d {s.Hours} h";
    }
}
