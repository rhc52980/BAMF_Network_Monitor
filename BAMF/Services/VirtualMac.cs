namespace LanWatch.Services;

/// <summary>
/// The MAC prefixes hypervisors and container runtimes give virtual network
/// cards. A device with one of these is almost certainly a VM (or a
/// container) rather than a physical box, so BAMF can say so and suggest it
/// for a virtual switch. Some are locally administered (QEMU/KVM, Docker), the
/// same bit phones set when they randomise their MAC, so this is also what
/// keeps those VMs from being filed away as randomised phones.
/// </summary>
public static class VirtualMac
{
    private static readonly (string Prefix, string Platform)[] Known =
    {
        ("BC2411", "Proxmox"),
        ("525400", "QEMU/KVM"),
        ("005056", "VMware"), ("000C29", "VMware"), ("000569", "VMware"), ("001C14", "VMware"),
        ("00155D", "Hyper-V"),
        ("080027", "VirtualBox"),
        ("00163E", "Xen"),
        ("001C42", "Parallels"),
        ("0242", "Docker"),
    };

    /// <summary>The platform whose prefix the MAC carries, or null.</summary>
    public static string? Platform(string mac)
    {
        var clean = (mac ?? "").Replace(":", "").Replace("-", "").ToUpperInvariant();
        foreach (var (prefix, platform) in Known)
            if (clean.StartsWith(prefix, StringComparison.Ordinal)) return platform;
        return null;
    }

    /// <summary>A device guess for a virtual MAC, in the "name (evidence)" form the others use.</summary>
    public static string Guess(string platform) =>
        platform == "Docker" ? "Container (Docker MAC)" : $"Virtual machine ({platform} MAC)";
}
