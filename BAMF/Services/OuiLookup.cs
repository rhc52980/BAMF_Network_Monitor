namespace LanWatch.Services;

/// <summary>
/// Resolves MAC address prefixes (OUI) to vendor names.
///
/// Load order:
///   1. oui.csv next to the exe (the IEEE registry), if present.
///   2. Otherwise a small built-in table is used immediately, and - unless
///      Bamf:AutoDownloadOui is false - the full IEEE list is downloaded in
///      the background from https://standards-oui.ieee.org/oui/oui.csv and
///      saved for future startups. Vendors on existing hosts fill in on the
///      next scan after the download completes.
/// </summary>
public class OuiLookup
{
    private const string OuiUrl = "https://standards-oui.ieee.org/oui/oui.csv";

    private Dictionary<string, string> _map;   // reference-swapped atomically
    private readonly string _csvPath;
    private readonly ILogger<OuiLookup> _log;
    private readonly IHttpClientFactory _httpFactory;

    public OuiLookup(ILogger<OuiLookup> log, IConfiguration config, IHttpClientFactory httpFactory)
    {
        _log = log;
        _httpFactory = httpFactory;
        _csvPath = Path.Combine(AppContext.BaseDirectory, "oui.csv");

        var fromFile = File.Exists(_csvPath) ? TryParseCsv(_csvPath) : null;
        if (fromFile is { Count: > 100 })
        {
            _map = fromFile;
            _log.LogInformation("Loaded {Count} OUI entries from oui.csv", fromFile.Count);
            return;
        }

        _map = new Dictionary<string, string>(BuiltIn);
        _log.LogInformation("Using built-in OUI table ({Count} entries)", _map.Count);

        if (config.GetValue("Bamf:AutoDownloadOui", true))
            _ = DownloadRegistryAsync();
    }

    private async Task DownloadRegistryAsync()
    {
        try
        {
            _log.LogInformation("Downloading IEEE OUI registry in the background...");
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            var csv = await client.GetStringAsync(OuiUrl);

            var tmp = _csvPath + ".tmp";
            await File.WriteAllTextAsync(tmp, csv);
            var parsed = TryParseCsv(tmp);
            if (parsed is { Count: > 100 })
            {
                File.Move(tmp, _csvPath, overwrite: true);
                _map = parsed; // atomic reference swap; readers see old or new, never partial
                _log.LogInformation("OUI registry downloaded: {Count} vendors. Names fill in on the next scan.", parsed.Count);
            }
            else
            {
                File.Delete(tmp);
                _log.LogWarning("Downloaded OUI file didn't parse; keeping built-in table");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("OUI registry download failed ({Message}); keeping built-in table. " +
                            "You can place oui.csv next to the exe manually.", ex.Message);
        }
    }

    public string Lookup(string mac)
    {
        var map = _map; // snapshot the reference
        var clean = mac.Replace(":", "").Replace("-", "").ToUpperInvariant();
        if (clean.Length < 6) return "";

        // Locally administered bit set => randomized MAC (phones doing privacy randomization)
        if (int.TryParse(clean[..2], System.Globalization.NumberStyles.HexNumber, null, out var firstByte)
            && (firstByte & 0x02) != 0 && (firstByte & 0x01) == 0)
            return "(randomized MAC)";

        return map.TryGetValue(clean[..6], out var vendor) ? vendor : "";
    }

    private Dictionary<string, string>? TryParseCsv(string path)
    {
        try
        {
            var map = new Dictionary<string, string>();
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                // Registry,Assignment,"Organization Name",Organization Address
                var parts = SplitCsv(line);
                if (parts.Count >= 3 && parts[1].Length == 6)
                    map[parts[1].ToUpperInvariant()] = parts[2].Trim();
            }
            return map;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse {Path}", path);
            return null;
        }
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes) { result.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        result.Add(cur.ToString());
        return result;
    }

    private static readonly Dictionary<string, string> BuiltIn = new()
    {
        ["00155D"] = "Microsoft (Hyper-V)",
        ["000C29"] = "VMware",
        ["005056"] = "VMware",
        ["080027"] = "Oracle VirtualBox",
        ["B827EB"] = "Raspberry Pi Foundation",
        ["DCA632"] = "Raspberry Pi Trading",
        ["E45F01"] = "Raspberry Pi Trading",
        ["001132"] = "Synology",
        ["0011D8"] = "ASUSTek",
        ["3C5282"] = "HP Inc.",
        ["F0272D"] = "Amazon Technologies",
        ["747548"] = "Amazon Technologies",
        ["18B430"] = "Nest Labs",
        ["641666"] = "Nest Labs",
        ["AC84C6"] = "TP-Link",
        ["50C7BF"] = "TP-Link",
        ["9C532E"] = "TP-Link",
        ["F4F26D"] = "TP-Link",
        ["001A11"] = "Google",
        ["F4F5D8"] = "Google",
        ["30FD38"] = "Google",
        ["A47733"] = "Google",
        ["3C22FB"] = "Apple",
        ["F0189E"] = "Apple",
        ["A85C2C"] = "Apple",
        ["BC9FEF"] = "Apple",
        ["D89E3F"] = "Apple",
        ["286D97"] = "Samsung",
        ["8C7712"] = "Samsung",
        ["5C497D"] = "Samsung",
        ["FCA621"] = "Samsung",
        ["D8BBC1"] = "Micro-Star International",
        ["309C23"] = "Micro-Star International",
        ["1C697A"] = "EliteGroup",
        ["4CCC6A"] = "Micro-Star International",
        ["00D861"] = "Micro-Star International",
        ["745D22"] = "Ubiquiti",
        ["788A20"] = "Ubiquiti",
        ["FCECDA"] = "Ubiquiti",
        ["24A43C"] = "Ubiquiti",
        ["B4FBE4"] = "Ubiquiti",
        ["0018DD"] = "Silicondust (HDHomeRun)",
        ["001CC0"] = "Intel",
        ["3C7C3F"] = "ASUSTek",
        ["04421A"] = "ASUSTek",
        ["2CF05D"] = "Micro-Star International",
        ["9C6B00"] = "ASRock",
        ["7085C2"] = "ASRock",
        ["E0D55E"] = "GIGA-BYTE",
        ["1C1B0D"] = "GIGA-BYTE",
        ["18C04D"] = "GIGA-BYTE",
        ["B42E99"] = "GIGA-BYTE",
        ["001B21"] = "Intel",
        ["A0369F"] = "Intel",
        ["3497F6"] = "ASUSTek",
        ["708BCD"] = "ASUSTek",
        ["C87F54"] = "ASUSTek",
    };
}
