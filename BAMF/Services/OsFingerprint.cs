namespace LanWatch.Services;

/// <summary>
/// Best-effort device/OS identification from signals BAMF can cheaply observe.
/// This is a heuristic, not nmap: it reports what the evidence suggests and
/// names that evidence, so a wrong guess is obvious rather than authoritative.
/// </summary>
public static class OsFingerprint
{
    /// <summary>
    /// Vendor + hostname only — costs nothing, runs on every scan. Returns ""
    /// when the vendor says nothing useful, rather than guessing wildly.
    /// </summary>
    public static string Passive(string vendor, string hostname)
    {
        var v = (vendor ?? "").ToLowerInvariant();
        var h = (hostname ?? "").ToLowerInvariant();

        if (v.Contains("raspberry")) return "Raspberry Pi (vendor)";
        if (v.Contains("apple")) return "Apple device (vendor)";
        if (v.Contains("ubiquiti") || v.Contains("mikrotik") || v.Contains("tp-link") ||
            v.Contains("netgear") || v.Contains("cisco") || v.Contains("aruba"))
            return "Network gear (vendor)";
        if (v.Contains("hewlett") || v.Contains("canon") || v.Contains("epson") ||
            v.Contains("brother") || v.StartsWith("hp inc"))
            return "Printer or HP device (vendor)";
        if (v.Contains("vizio") || v.Contains("roku") || v.Contains("samsung electronics") ||
            v.Contains("lg electronics"))
            return "TV or media device (vendor)";
        if (v.Contains("amazon")) return "Amazon device (vendor)";
        if (v.Contains("google") || v.Contains("nest")) return "Google/Nest device (vendor)";
        if (v.Contains("espressif")) return "ESP32/ESP8266 IoT (vendor)";
        if (v.Contains("synology") || v.Contains("qnap")) return "NAS (vendor)";

        if (h.Contains("iphone")) return "iPhone (hostname)";
        if (h.Contains("ipad")) return "iPad (hostname)";
        if (h.Contains("android")) return "Android (hostname)";

        return "";
    }

    /// <summary>
    /// Full guess, combining the TTL of an ICMP reply with open ports and the
    /// passive signals. Only runs when the user asks for it.
    /// </summary>
    public static string Active(int? ttl, string vendor, string hostname, IReadOnlyCollection<int> openPorts)
    {
        var evidence = new List<string>();
        string? os = null;

        // Hosts on the same L2 segment are 0-1 hops away, so the observed TTL is
        // effectively the sender's initial TTL.
        if (ttl is int t)
        {
            evidence.Add($"TTL {t}");
            os = t switch
            {
                > 0 and <= 64 => "Linux/Unix (incl. Android, iOS, most IoT)",
                > 64 and <= 128 => "Windows",
                > 128 => "Network gear or BSD/printer",
                _ => null,
            };
        }

        // Open ports refine or override the TTL family.
        if (openPorts.Contains(62078)) { os = "iPhone/iPad (iOS)"; evidence.Add("iOS lockdown port"); }
        else if (openPorts.Contains(3389)) { os = "Windows"; evidence.Add("RDP"); }
        else if (openPorts.Contains(445) || openPorts.Contains(139))
        {
            evidence.Add("SMB");
            os ??= "Windows or SMB file server";
            if (os.StartsWith("Windows")) os = "Windows";
        }
        else if (openPorts.Contains(9100) || openPorts.Contains(631))
        {
            os = "Printer"; evidence.Add("print service");
        }
        else if (openPorts.Contains(22)) { evidence.Add("SSH"); os ??= "Linux/Unix"; }

        if (openPorts.Contains(32400)) evidence.Add("Plex");

        // Fall back to whatever the free signals said.
        if (os is null)
        {
            var passive = Passive(vendor, hostname);
            if (passive != "") return passive;
            return evidence.Count > 0 ? "Unknown (" + string.Join(", ", evidence) + ")" : "Unknown";
        }

        return evidence.Count > 0 ? $"{os} ({string.Join(", ", evidence)})" : os;
    }
}
