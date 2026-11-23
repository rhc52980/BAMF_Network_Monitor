using System.Text.RegularExpressions;

namespace LanWatch.Services;

/// <summary>
/// The segmentation check: rules that say which devices belong on which network ("cameras belong on 192.168.20.0/24", "smart plugs
/// never on 192.168.1.0/24"), and a list of the devices that break them. A rule picks devices by a tag you gave them, by the device
/// type or Map icon you set, or by words in their name, vendor or BAMF's guess; any of those matches. Off until you switch it on, and
/// the alert for a new device breaking a rule is a second switch, also off. It only looks: it moves nothing.
/// </summary>
public sealed class SegmentationCheck : BackgroundService
{
    public sealed record Rule(string Id, string Label, string Tag, List<string> Words, string Type, string Where, string Network);
    public sealed record Finding(string RuleId, string Rule, long HostId, string Name, string Ip, string Network, string Expected, string Where, string Why);

    public const int MaxRules = 20, MaxWords = 10, MaxWordLength = 30;
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly ILogger<SegmentationCheck> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);

    public SegmentationCheck(HostStore store, ScannerService scanner, ILogger<SegmentationCheck> log)
    {
        _store = store; _scanner = scanner; _log = log;
    }

    public bool Enabled => _store.GetSetting("segOn") == "true";
    public bool AlertOn => Enabled && _store.GetSetting("segAlert") == "true";
    public List<Rule> Rules => _store.GetJsonList<Rule>("segRules");

    /// <summary>The rules as typed, made safe to keep, or an error to show. A rule needs a name, somewhere, and something to pick devices by.</summary>
    public static (List<Rule>? Rules, string? Error) Clean(IEnumerable<Rule>? rules)
    {
        var list = new List<Rule>();
        foreach (var r in rules ?? Array.Empty<Rule>())
        {
            var label = (r.Label ?? "").Trim();
            if (label.Length == 0) return (null, "Give each rule a name.");
            if (label.Length > 40) label = label[..40];
            var tag = (r.Tag ?? "").Trim();
            if (tag.Length > HostStore.MaxTagLength) return (null, $"A tag is at most {HostStore.MaxTagLength} characters.");
            var words = (r.Words ?? new()).Select(w => (w ?? "").Trim()).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (words.Count > MaxWords) return (null, $"At most {MaxWords} words in a rule.");
            if (words.Any(w => w.Length > MaxWordLength)) return (null, $"A word is at most {MaxWordLength} characters.");
            var type = (r.Type ?? "").Trim();
            if (type.Length > HostStore.MaxTypeName) return (null, "That device type is too long.");
            if (tag == "" && words.Count == 0 && type == "") return (null, $"\"{label}\" needs a tag, a device type or some words to pick devices by.");
            var where = (r.Where ?? "").Trim().ToLowerInvariant();
            if (where is not ("on" or "off")) return (null, "Each rule says whether the devices belong on a network or never on it.");
            var network = (r.Network ?? "").Trim();
            if (network.Length == 0 || network.Length > 50) return (null, $"Pick the network for \"{label}\".");
            list.Add(new Rule(string.IsNullOrWhiteSpace(r.Id) ? Guid.NewGuid().ToString("N")[..8] : r.Id.Trim(), label, tag, words, type, where, network));
        }
        if (list.Count > MaxRules) return (null, $"At most {MaxRules} rules.");
        return (list, null);
    }

    /// <summary>Whether a rule picks a device, and what it matched on, for the list.</summary>
    public static bool Matches(Rule r, HostRecord h, IReadOnlyCollection<string>? tags, string? icon, string? typeName, out string why)
    {
        why = "";
        if (r.Tag != "" && tags is not null && tags.Contains(r.Tag, StringComparer.OrdinalIgnoreCase)) { why = $"tagged {r.Tag}"; return true; }
        if (r.Type != "" && ((icon ?? "").Equals(r.Type, StringComparison.OrdinalIgnoreCase) || (typeName ?? "").Equals(r.Type, StringComparison.OrdinalIgnoreCase))) { why = $"type {r.Type}"; return true; }
        if (r.Words.Count > 0)
        {
            var text = string.Join(" ", new[] { h.OsGuess, h.CustomName, h.Hostname == "—" ? "" : h.Hostname, h.MdnsName, h.Vendor, typeName ?? "" }.Where(s => !string.IsNullOrEmpty(s)));
            foreach (var w in r.Words)
                if (Regex.IsMatch(text, @"(?<![A-Za-z0-9])" + Regex.Escape(w) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                { why = $"named like \"{w}\""; return true; }
        }
        return false;
    }

    /// <summary>Every device that breaks a rule, as of now.</summary>
    public List<Finding> Evaluate()
    {
        var rules = Rules;
        if (rules.Count == 0) return new();
        var tags = _store.GetTags();
        var icons = _store.GetDeviceTypes();
        var names = _store.GetTypeNames();
        var found = new List<Finding>();
        foreach (var h in _store.GetAll().Where(h => !h.Forgotten && !h.Ignored && h.Subnet != ""))
        {
            foreach (var r in rules)
            {
                if (!Matches(r, h, tags.GetValueOrDefault(h.Id), icons.GetValueOrDefault(h.Id), names.GetValueOrDefault(h.Id), out var why)) continue;
                var wrong = r.Where == "on" ? !h.Subnet.Equals(r.Network, StringComparison.OrdinalIgnoreCase) : h.Subnet.Equals(r.Network, StringComparison.OrdinalIgnoreCase);
                if (wrong) found.Add(new Finding(r.Id, r.Label, h.Id, Cutover.NameOf(h), h.Ip, h.Subnet, r.Network, r.Where, why));
            }
        }
        return found.OrderBy(f => f.Rule, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Ip, StringComparer.Ordinal).ToList();
    }

    private static string Key(Finding f) => $"{f.RuleId}|{f.HostId}|{f.Network}";

    /// <summary>Remembers what already breaks a rule as alerted, so only what turns up afterwards is announced: used when the alert is switched on.</summary>
    public void Baseline() => _store.SetJsonList("segAlerted", Evaluate().Select(Key));

    /// <summary>Looks now; raises one alert for each device that newly breaks a rule (one summary when many do). Returns the findings.</summary>
    public async Task<List<Finding>> Run(CancellationToken ct)
    {
        await _busy.WaitAsync(ct);
        try
        {
            var findings = Evaluate();
            if (!AlertOn) return findings;
            var known = _store.GetJsonList<string>("segAlerted").ToHashSet();
            var fresh = findings.Where(f => !known.Contains(Key(f))).ToList();
            _store.SetJsonList("segAlerted", findings.Select(Key));      // what has been put right is forgotten, so it alerts again if it comes back
            if (fresh.Count == 0) return findings;
            if (fresh.Count <= 3)
                foreach (var f in fresh) await Raise(f, ct);
            else
            {
                var title = $"{fresh.Count} devices are on the wrong network";
                var detail = string.Join("; ", fresh.Take(6).Select(f => $"{f.Name} is on {_store.AlertNetName(f.Network)}, {Wording(f)}")) + (fresh.Count > 6 ? $"; and {fresh.Count - 6} more" : "") + ". Your segmentation rules are under Settings, Your network.";
                _store.AddAlert("security", title, detail);
                await _scanner.SendGenericAlert(title, detail, "security", ct);
            }
            return findings;
        }
        finally { _busy.Release(); }
    }

    private string Wording(Finding f) => f.Where == "on" ? $"where {f.Rule} belong on {_store.AlertNetName(f.Expected)}" : $"where {f.Rule} should never be";

    private async Task Raise(Finding f, CancellationToken ct)
    {
        var title = $"{f.Name} is on {_store.AlertNetName(f.Network)}, outside your \"{f.Rule}\" rule";
        var detail = $"{f.Name} ({f.Ip}) matched your rule \"{f.Rule}\" ({f.Why}) and is on {_store.AlertNetName(f.Network)}, {Wording(f)}. " +
                     "If it is where it should be, change the rule or give the device a tag the rule doesn't pick; if not, it may be on the wrong Wi-Fi network or switch port.";
        _log.LogWarning("Segmentation: {Title}", title);
        _store.AddAlert("security", title, detail);
        await _scanner.SendGenericAlert(title, detail, "security", ct, hostId: f.HostId);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { if (Enabled) await Run(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "The segmentation check failed"); }
            try { await Task.Delay(Every, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
