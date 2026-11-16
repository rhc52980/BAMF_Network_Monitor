namespace LanWatch.Services;

/// <summary>
/// The cutover check: what the network looked like before a change, against what it looks like now. Matched by MAC address, so a
/// device that came back at a new address is "moved", not "missing". A device that has gone but has a new one of the same name is
/// offered as probably the same device (a phone with a private Wi-Fi address on the new network is a different MAC).
/// </summary>
public static class Cutover
{
    public sealed record Item(string Mac, string Name, string Ip, string Network, string WasIp, string WasNetwork, string LastSeen);
    public sealed record Pair(Item Was, Item Now);
    public sealed record Result(int WasOnline, int WasOffline, List<Item> Back, List<Item> Moved, List<Item> Missing, List<Item> New, List<Pair> SameName);

    /// <summary>The name to show for a device: yours, else the one it gave itself, else its address.</summary>
    public static string NameOf(HostRecord h) => h.CustomName != "" ? h.CustomName : h.Hostname is { Length: > 0 } and not "—" ? h.Hostname : h.Ip;

    /// <summary>What to keep of every device BAMF knows, hidden ones left out.</summary>
    public static List<HostStore.SnapDevice> Take(IEnumerable<HostRecord> hosts) =>
        hosts.Where(h => !h.Forgotten && h.Mac != "").OrderBy(h => h.Ip, StringComparer.Ordinal)
            .Select(h => new HostStore.SnapDevice(h.Mac.ToUpperInvariant(), h.Ip, NameOf(h), h.Subnet, h.Online, h.Vendor)).ToList();

    private static bool Named(string name, string ip) => name != "" && name != ip && !System.Net.IPAddress.TryParse(name, out _);

    public static Result Compare(IReadOnlyList<HostStore.SnapDevice> then, IReadOnlyList<HostRecord> now)
    {
        var current = now.Where(h => h.Mac != "").GroupBy(h => h.Mac.ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First());
        var before = then.GroupBy(d => d.Mac.ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First());
        var back = new List<Item>(); var moved = new List<Item>(); var missing = new List<Item>();

        foreach (var d in before.Values.Where(d => d.Online))
        {
            if (current.TryGetValue(d.Mac.ToUpperInvariant(), out var h) && !h.Ignored && h.Online && !h.Forgotten)
            {
                var item = new Item(d.Mac, NameOf(h), h.Ip, h.Subnet, d.Ip, d.Network, h.LastSeen);
                if (h.Ip == d.Ip && h.Subnet == d.Network) back.Add(item); else moved.Add(item);
            }
            else if (!(current.TryGetValue(d.Mac.ToUpperInvariant(), out var gone) && gone.Ignored))
                missing.Add(new Item(d.Mac, d.Name, d.Ip, d.Network, d.Ip, d.Network, current.TryGetValue(d.Mac.ToUpperInvariant(), out var seen) ? seen.LastSeen : ""));
        }

        // Online now and not in the snapshot at all: new to BAMF's list, or on the network now and not before.
        var added = now.Where(h => h.Mac != "" && h.Online && !h.Ignored && !h.Forgotten && !before.ContainsKey(h.Mac.ToUpperInvariant()))
            .Select(h => new Item(h.Mac.ToUpperInvariant(), NameOf(h), h.Ip, h.Subnet, "", "", h.LastSeen)).ToList();

        // A missing device and a new one with the same name: very likely one device with a new hardware address.
        var pairs = new List<Pair>();
        foreach (var m in missing.ToList())
        {
            if (!Named(m.Name, m.Ip)) continue;
            var twin = added.FirstOrDefault(a => a.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase));
            if (twin is null) continue;
            pairs.Add(new Pair(m, twin));
            missing.Remove(m); added.Remove(twin);
        }

        return new Result(before.Values.Count(d => d.Online), before.Values.Count(d => !d.Online), back, moved, missing, added, pairs);
    }
}
