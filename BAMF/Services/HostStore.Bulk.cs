namespace LanWatch.Services;

/// <summary>
/// One change to many devices at once, for the list's Select mode: mark known or unknown, watch, ignore, forget, snooze,
/// add or remove a tag. Each does exactly what the same choice in a device's own menu does, one device at a time, and a
/// device that can't take it (it's gone, or it has too many tags already) is reported rather than stopping the rest.
/// </summary>
public partial class HostStore
{
    public static readonly string[] BulkActions =
        { "known", "unknown", "watch", "unwatch", "ignore", "unignore", "forget", "restore", "snooze", "unsnooze", "tag", "untag" };

    public const int MaxBulkDevices = 500;

    /// <summary>Whether Select mode is offered. On unless switched off in Settings.</summary>
    public bool BulkEnabled => GetSetting("bulkSelect") != "false";

    /// <summary>
    /// How a device stood before a bulk change: what Undo puts back. <c>Tags</c> null leaves tags alone; <c>SnoozedUntil</c>
    /// null means not snoozed.
    /// </summary>
    public sealed record HostState(long Id, bool Known, bool Watched, bool Ignored, bool Forgotten, List<string>? Tags, string? SnoozedUntil);

    public sealed record BulkFailure(long Id, string Error);
    public sealed record BulkResult(int Done, int Missing, IReadOnlyList<BulkFailure> Failed);

    /// <summary>
    /// Puts each device back to the state given, the way Undo needs. Forgotten goes first, because forgetting a device also stops
    /// watching it, and the state's own watched flag has to win. A device that has gone is counted, and the rest are done.
    /// </summary>
    public (BulkResult? Result, string? Error) RestoreStates(IEnumerable<HostState>? states)
    {
        var list = (states ?? []).GroupBy(s => s.Id).Select(g => g.Last()).ToList();
        if (list.Count == 0) return (null, "Nothing to put back.");
        if (list.Count > MaxBulkDevices) return (null, $"Up to {MaxBulkDevices} devices at a time.");
        var byId = GetAll().ToDictionary(h => h.Id);
        var failed = new List<BulkFailure>();
        int done = 0, missing = 0;
        foreach (var s in list)
        {
            if (!byId.TryGetValue(s.Id, out var h)) { missing++; continue; }
            SetForgotten(s.Id, s.Forgotten);
            SetKnown(s.Id, s.Known);
            SetWatched(s.Id, s.Watched);
            SetIgnored(s.Id, s.Ignored);
            string? error = null;
            if (s.Tags is not null) error = SetTags(s.Id, s.Tags);
            if (DateTime.TryParse(s.SnoozedUntil, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var until) && until.ToUniversalTime() > DateTime.UtcNow)
                SnoozeHost(s.Id, until.ToUniversalTime(), h.Online);
            else Unsnooze(s.Id);
            if (error is not null) failed.Add(new BulkFailure(s.Id, error)); else done++;
        }
        return (new BulkResult(done, missing, failed), null);
    }

    /// <summary>Applies <paramref name="action"/> to each device. Returns an error for the request as a whole, or null.</summary>
    public (BulkResult? Result, string? Error) Bulk(IEnumerable<long>? ids, string? action, string? tag, int? minutes)
    {
        action = (action ?? "").Trim().ToLowerInvariant();
        if (!BulkActions.Contains(action)) return (null, "That isn't something BAMF can do to several devices at once.");
        var list = (ids ?? []).Distinct().ToList();
        if (list.Count == 0) return (null, "Pick at least one device.");
        if (list.Count > MaxBulkDevices) return (null, $"Up to {MaxBulkDevices} devices at a time.");
        tag = (tag ?? "").Trim();
        if (action is "tag" or "untag")
        {
            if (tag.Length == 0) return (null, "Which tag?");
            if (tag.Length > MaxTagLength) return (null, $"Tags can be at most {MaxTagLength} characters.");
            if (tag.Contains(',')) return (null, "A tag can't contain a comma.");
        }
        if (action == "snooze" && minutes is not (> 0 and <= MaxSnoozeMinutes)) return (null, "A week at most.");

        var byId = GetAll().ToDictionary(h => h.Id);
        var tags = action is "tag" or "untag" ? GetTags() : null;
        var failed = new List<BulkFailure>();
        int done = 0, missing = 0;
        foreach (var id in list)
        {
            if (!byId.TryGetValue(id, out var h)) { missing++; continue; }
            bool ok = true; string? error = null;
            switch (action)
            {
                case "known": ok = SetKnown(id, true); break;
                case "unknown": ok = SetKnown(id, false); break;
                case "watch": ok = SetWatched(id, true); break;
                case "unwatch": ok = SetWatched(id, false); break;
                case "ignore": ok = SetIgnored(id, true); break;
                case "unignore": ok = SetIgnored(id, false); break;
                case "forget": ok = SetForgotten(id, true); break;
                case "restore": ok = SetForgotten(id, false); break;
                case "snooze": SnoozeHost(id, DateTime.UtcNow.AddMinutes(minutes!.Value), h.Online); break;
                case "unsnooze": Unsnooze(id); break;
                case "tag":
                case "untag":
                    var have = tags!.TryGetValue(id, out var t) ? t : new List<string>();
                    var next = action == "tag"
                        ? (have.Any(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase)) ? have : [.. have, tag])
                        : have.Where(x => !string.Equals(x, tag, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (next.Count != have.Count || action == "tag") error = SetTags(id, next);
                    break;
            }
            if (!ok) missing++;
            else if (error is not null) failed.Add(new BulkFailure(id, error));
            else done++;
        }
        return (new BulkResult(done, missing, failed), null);
    }
}
