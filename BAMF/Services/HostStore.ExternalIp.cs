using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// The home's public address, as the internet sees it. BAMF never goes looking for it on its own: it
/// learns it from two things it already does, the speed test (Cloudflare says which address a request
/// came from) and the daily GreyNoise check (which has to learn the address to look it up). Whichever
/// is newest wins. What's kept is the address, where it came from, when it was last confirmed, since
/// when it has been this address, and what it was before it changed.
/// </summary>
public partial class HostStore
{
    public sealed record ExternalIp(string Ip, string Source, string At, string Since, string? Previous, string? ChangedAt);

    public ExternalIp? GetExternalIp()
    {
        try { return JsonSerializer.Deserialize<ExternalIp>(GetSetting("externalIp") ?? "null"); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Notes the public address a check just learned. Anything that isn't a public address is ignored.
    /// True if it's a different address from the one before (the first one learned doesn't count).
    /// </summary>
    public bool RecordExternalIp(string? text, string source, DateTime? at = null)
    {
        text = (text ?? "").Trim();
        // .NET reads "1.2.3" as an address; an IPv4 answer has to be four numbers.
        if (!IPAddress.TryParse(text, out var ip) || (ip.AddressFamily == AddressFamily.InterNetwork && text.Count(c => c == '.') != 3) || !IsPublicAddress(ip)) return false;
        var now = (at ?? DateTime.UtcNow).ToString("o");
        var was = GetExternalIp();
        var address = ip.ToString();
        ExternalIp next;
        bool changed;
        if (was is null) { next = new ExternalIp(address, source, now, now, null, null); changed = false; }
        else if (was.Ip == address) { next = was with { Source = source, At = now }; changed = false; }
        else { next = new ExternalIp(address, source, now, now, was.Ip, now); changed = true; }
        SetSetting("externalIp", JsonSerializer.Serialize(next));
        return changed;
    }

    /// <summary>A public address: not private, loopback, link-local, unspecified or multicast. A shared (carrier-grade NAT) address counts.</summary>
    internal static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC);
        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || b[0] >= 224);
    }
}
