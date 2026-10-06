namespace LanWatch.Services;

/// <summary>
/// Which watched devices that changed state in one scan are said one by one and which are said together. When a switch, access point
/// or router you recorded goes offline, everything plugged into it goes too, and used to send an alert each. Here the devices behind a
/// switch that is itself offline are gathered under it, so the alert says "Rack switch went offline, and 14 devices behind it" once. It
/// only knows the wiring you recorded: a device with no recorded place, or behind a switch that has no device record of its own, is
/// said on its own as before. Pure: given the scan's changes and the wiring, no database and no clock.
/// </summary>
public static class StatusAlertPlanner
{
    /// <summary>A switch (or AP, router) that is down, the watched devices behind it that changed too, and how many are behind it in all.</summary>
    public sealed record Group(SwitchRecord Root, HostRecord? RootHost, List<HostRecord> Behind, int BehindTotal, bool Up);

    public sealed record Plan(List<HostRecord> Single, List<Group> Groups);

    /// <summary>
    /// <paramref name="changed"/> went down (or, with <paramref name="up"/>, came back) this scan. A group needs the switch's own device among
    /// them and one behind it, or two behind it; anything smaller is said individually.
    /// </summary>
    public static Plan Make(IReadOnlyList<HostRecord> changed, bool up, IReadOnlyList<SwitchRecord> switches,
        IReadOnlyDictionary<long, (long SwitchId, int Port)> placements, IReadOnlyDictionary<long, HostRecord> hosts)
    {
        var bySwitch = switches.ToDictionary(s => s.Id);
        var changedIds = changed.Select(h => h.Id).ToHashSet();

        // A switch counts as down when its own device went down this scan or is offline now (or, coming back, is among those that came back).
        bool Down(SwitchRecord s) => s.HostId > 0 && (up ? changedIds.Contains(s.HostId) : hosts.TryGetValue(s.HostId, out var h) && !h.Online);

        // The topmost switch above this one that is down, so a rack switch behind a dead core switch is said under the core switch.
        SwitchRecord? RootOf(long switchId)
        {
            SwitchRecord? root = null;
            var cur = bySwitch.GetValueOrDefault(switchId);
            for (var guard = 0; cur is not null && guard < 16; guard++)
            {
                if (Down(cur)) root = cur;
                cur = cur.UplinkSwitch > 0 ? bySwitch.GetValueOrDefault(cur.UplinkSwitch) : null;
            }
            return root;
        }

        var behind = new Dictionary<long, List<HostRecord>>();     // root switch id -> the changed devices behind it
        var rootHosts = new Dictionary<long, HostRecord>();
        foreach (var h in changed)
        {
            if (!placements.TryGetValue(h.Id, out var place)) continue;
            if (RootOf(place.SwitchId) is not { } root || root.HostId == h.Id) continue;
            (behind.TryGetValue(root.Id, out var list) ? list : behind[root.Id] = new()).Add(h);
        }
        foreach (var s in switches)
            if (s.HostId > 0 && changedIds.Contains(s.HostId) && hosts.TryGetValue(s.HostId, out var sh) && RootOf(s.Id)?.Id == s.Id && behind.ContainsKey(s.Id))
                rootHosts[s.Id] = sh;

        var groups = new List<Group>();
        var grouped = new HashSet<long>();
        foreach (var (rootId, members) in behind)
        {
            rootHosts.TryGetValue(rootId, out var rootHost);
            if (!(rootHost is not null && members.Count >= 1 || members.Count >= 2)) continue;
            var root = bySwitch[rootId];
            // Everyone placed behind it that is in the same state now, watched or not, for the "and N devices behind it".
            var total = placements.Count(p => p.Key != root.HostId && RootOf(p.Value.SwitchId)?.Id == rootId
                && hosts.TryGetValue(p.Key, out var x) && !x.Ignored && !x.Forgotten && (up ? x.Online : !x.Online));
            groups.Add(new Group(root, rootHost, members, Math.Max(total, members.Count), up));
            foreach (var m in members) grouped.Add(m.Id);
            if (rootHost is not null) grouped.Add(rootHost.Id);
        }
        return new Plan(changed.Where(h => !grouped.Contains(h.Id)).ToList(), groups);
    }

    /// <summary>What the alert for a group says.</summary>
    public static (string Title, string Detail) Describe(Group g, Func<HostRecord, string> nameOf)
    {
        var rootName = g.RootHost is not null ? nameOf(g.RootHost) : g.Root.Name;
        var shown = g.Behind.Take(8).Select(nameOf).ToList();
        var more = g.Behind.Count > shown.Count ? $" and {g.Behind.Count - shown.Count} more" : "";
        var list = string.Join(", ", shown) + more;
        var n = g.BehindTotal;
        var devices = n == 1 ? "1 device" : $"{n} devices";
        if (g.Up)
            return g.RootHost is not null
                ? ($"{rootName} is back online, and so are {devices} behind it", $"{rootName} came back, and watched devices behind it did too: {list}.")
                : ($"{devices} behind {g.Root.Name} are back online", $"Watched devices that were behind {g.Root.Name}: {list}.");
        return g.RootHost is not null
            ? ($"{rootName} went offline, and {devices} behind it", $"{rootName} stopped responding, and so did the watched devices behind it: {list}. It is the likeliest cause, so check it first.")
            : ($"{g.Behind.Count} watched devices went offline together", $"{list}: all behind {g.Root.Name}, which is offline. Check it first.");
    }
}
