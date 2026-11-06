namespace LanWatch.Services;

/// <summary>
/// "Same phone, new address?": a phone that makes up a new MAC address now and then turns up as a new device each time, with the name it always
/// had. This finds the records that share a name and have never been online together, and offers them as one device. It only suggests: nothing is
/// combined until someone says so, and combining is the ordinary kind (Network cards in a device's menu), so Separate undoes it.
/// </summary>
public partial class HostStore
{
    public sealed record SameMember(long Id, string Mac, string Ip, string Subnet, bool Online, bool Ignored, string FirstSeen, string LastSeen);
    /// <summary>One suggestion: the records that look like one device, and the one the others would join (the one seen most recently).</summary>
    public sealed record SameGroup(string Key, string Name, long KeepId, List<SameMember> Members);

    /// <summary>Names every phone has by default. Several different phones share one, so it says nothing about whether two records are the same.</summary>
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "iphone", "ipad", "android", "galaxy", "pixel", "phone", "localhost", "unknown", "mobile", "tablet", "-", "—",
    };

    /// <summary>More records than this under one name is a generic name nobody listed, not one phone.</summary>
    public const int MostInAGroup = 8;

    public bool SameDeviceHintsEnabled => GetSetting("sameDeviceHints") != "false";

    /// <summary>The name two records are matched on: the one it was given, else its own name, else what it announced. Lower case, without a domain.</summary>
    internal static string SameName(HostRecord h)
    {
        var n = h.CustomName != "" ? h.CustomName : h.Hostname != "" ? h.Hostname : h.MdnsName;
        n = (n ?? "").Trim().ToLowerInvariant();
        foreach (var suffix in new[] { ".local", ".lan", ".home", ".localdomain" })
            if (n.EndsWith(suffix, StringComparison.Ordinal)) n = n[..^suffix.Length];
        return n;
    }

    /// <summary>
    /// Records with a made-up MAC address that share a name, never two online at once, and not already combined. A group's key is its name and
    /// the records in it, so it comes back if another record joins it after being dismissed.
    /// </summary>
    public List<SameGroup> SameDeviceGroups()
    {
        var dismissed = Dismissed();
        var joined = GetInterfaces();
        var groups = new List<SameGroup>();
        foreach (var g in GetAll()
            .Where(h => !h.Forgotten && h.Vendor == "(randomized MAC)" && !joined.ContainsKey(h.Id))
            .Select(h => (h, name: SameName(h)))
            .Where(x => x.name.Length >= 3 && !GenericNames.Contains(x.name))
            .GroupBy(x => x.name))
        {
            var members = g.Select(x => x.h).ToList();
            if (members.Count < 2 || members.Count > MostInAGroup) continue;
            if (members.Count(m => m.Online) > 1) continue;                    // two of them answering now are two devices
            var keep = members.OrderByDescending(m => m.Online).ThenByDescending(m => m.LastSeen, StringComparer.Ordinal).First();
            var key = g.Key + "|" + string.Join(",", members.Select(m => m.Id).OrderBy(i => i));
            if (dismissed.Contains(key)) continue;
            groups.Add(new SameGroup(key, g.Key, keep.Id, members
                .OrderByDescending(m => m.Id == keep.Id).ThenByDescending(m => m.LastSeen, StringComparer.Ordinal)
                .Select(m => new SameMember(m.Id, m.Mac, m.Ip, m.Subnet, m.Online, m.Ignored, m.FirstSeen, m.LastSeen)).ToList()));
        }
        return groups.OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
    }

    private HashSet<string> Dismissed()
    {
        try { return (System.Text.Json.JsonSerializer.Deserialize<List<string>>(GetSetting("sameDeviceDismissed") ?? "[]") ?? new()).ToHashSet(); }
        catch (System.Text.Json.JsonException) { return new(); }
    }

    /// <summary>Stops offering this group. If a record joins it later, the larger group is offered afresh.</summary>
    public bool DismissSameDevice(string key)
    {
        if (!SameDeviceGroups().Any(g => g.Key == key)) return false;
        var set = Dismissed();
        set.Add(key);
        SetSetting("sameDeviceDismissed", System.Text.Json.JsonSerializer.Serialize(set.TakeLast(200)));
        return true;
    }

    /// <summary>Combines a suggested group: the others become network cards of the one seen most recently. Only a group that is on offer can be combined.</summary>
    public (int Combined, string? Error) CombineSameDevice(string key)
    {
        var group = SameDeviceGroups().FirstOrDefault(g => g.Key == key);
        if (group is null) return (0, "That suggestion is out of date. The devices have changed since.");
        var done = 0;
        string? error = null;
        foreach (var m in group.Members.Where(m => m.Id != group.KeepId))
        {
            var e = SetInterfaceOf(m.Id, group.KeepId);
            if (e is null) done++; else error ??= e;
        }
        return (done, error);
    }
}
