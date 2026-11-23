using System.Text.Json;

namespace LanWatch.Services;

public partial class HostStore
{
    private const string NetworkLabelsSetting = "networkLabels";

    /// <summary>The names the user gave their networks: "192.168.30.0/24" to "IoT". Display only; devices stay filed by range.</summary>
    public Dictionary<string, string> GetNetworkLabels()
    {
        var raw = GetSetting(NetworkLabelsSetting);
        if (string.IsNullOrEmpty(raw)) return new(StringComparer.Ordinal);
        try { return new(JsonSerializer.Deserialize<Dictionary<string, string>>(raw) ?? new(), StringComparer.Ordinal); }
        catch (JsonException) { return new(StringComparer.Ordinal); }
    }

    /// <summary>Names a network, or with an empty label takes the name away. Returns an error, or null.</summary>
    public string? SetNetworkLabel(string? network, string? label)
    {
        var net = (network ?? "").Trim();
        if (net.Length == 0 || net.Length > 80) return "Which network is this for?";
        var text = string.Join(' ', (label ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > 40) return "Keep the name to 40 characters or fewer.";
        if (text.Any(char.IsControl)) return "A name can't hold control characters.";
        var labels = GetNetworkLabels();
        if (text.Length == 0) labels.Remove(net); else labels[net] = text;
        if (labels.Count > 200) return "That's more networks than BAMF keeps names for.";
        SetSetting(NetworkLabelsSetting, JsonSerializer.Serialize(labels));
        return null;
    }

    /// <summary>Whether alert text says "IoT · 192.168.30.0/24" rather than the range alone. Off unless switched on.</summary>
    public bool NetworkNamesInAlerts => GetSetting(NetworkNamesInAlertsSetting) == "true";

    private const string NetworkNamesInAlertsSetting = "networkNamesInAlerts";

    /// <summary>A network as an alert names it: the name you gave it, then its range, when the switch is on and it has a name; else the range alone.</summary>
    public string AlertNetName(string? network)
    {
        var net = network ?? "";
        if (net.Length == 0 || !NetworkNamesInAlerts) return net;
        return GetNetworkLabels().TryGetValue(net, out var label) ? label + " · " + net : net;
    }

    /// <summary>A network's name follows it to a new range, or goes when it goes.</summary>
    public void CarryNetworkLabel(string from, string? to)
    {
        var labels = GetNetworkLabels();
        if (!labels.Remove(from, out var text)) return;
        if (to is not null) labels[to] = text;
        SetSetting(NetworkLabelsSetting, JsonSerializer.Serialize(labels));
    }
}
