using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// Saved views of the device list: a name for a combination of tab, network, status, device type, tag and search, so
/// "Kids' devices offline" or "Unknown on the guest network" is one click instead of five. Kept on the server, so every
/// dashboard has the same ones. A view says what to show, nothing more: it never changes a device.
/// </summary>
public partial class HostStore
{
    public const int MaxViews = 20, MaxViewName = 40;
    private const string SavedViewsSetting = "savedViews";
    public static readonly string[] ViewTabs = { "devices", "forgotten" };
    public static readonly string[] ViewStatuses = { "all", "online", "offline", "unknown", "new", "ignored" };

    /// <summary>
    /// One saved view. <c>Network</c> is "all" or a network as the tabs name it, <c>Guess</c> a device-type chip (empty for
    /// every type, "__none__" for devices with no guess), <c>Tag</c> a tag chip (empty for every device), <c>Query</c> the search box.
    /// </summary>
    public sealed record SavedView(string Name, string Tab, string Network, string Status, string Guess, string Tag, string Query);

    public List<SavedView> GetViews()
    {
        try { return JsonSerializer.Deserialize<List<SavedView>>(GetSetting(SavedViewsSetting) ?? "[]") ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static bool Clean(string? s, int max) => s is null || (s.Length <= max && !s.Any(char.IsControl));

    /// <summary>Saves a view, replacing the one with the same name (any case). Returns an error, or null.</summary>
    public string? SaveView(SavedView v)
    {
        var name = (v.Name ?? "").Trim();
        if (name.Length == 0) return "Give the view a name.";
        if (name.Length > MaxViewName) return $"A name can be at most {MaxViewName} characters.";
        var tab = string.IsNullOrEmpty(v.Tab) ? "devices" : v.Tab;
        var status = string.IsNullOrEmpty(v.Status) ? "all" : v.Status;
        if (!ViewTabs.Contains(tab)) return "That isn't a tab a view can be on.";
        if (!ViewStatuses.Contains(status)) return "That isn't a status a view can show.";
        if (!Clean(name, MaxViewName) || !Clean(v.Network, 80) || !Clean(v.Guess, 80) || !Clean(v.Tag, MaxTagLength) || !Clean(v.Query, 100))
            return "Something in that view is too long or isn't text BAMF would save.";
        var views = GetViews();
        var clean = new SavedView(name, tab, string.IsNullOrWhiteSpace(v.Network) ? "all" : v.Network.Trim(), status, (v.Guess ?? "").Trim(), (v.Tag ?? "").Trim(), (v.Query ?? "").Trim());
        var i = views.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) views[i] = clean;
        else
        {
            if (views.Count >= MaxViews) return $"BAMF keeps up to {MaxViews} views. Delete one first.";
            views.Add(clean);
        }
        SetSetting(SavedViewsSetting, JsonSerializer.Serialize(views));
        return null;
    }

    /// <summary>Removes a view by name. False if there isn't one.</summary>
    public bool DeleteView(string? name)
    {
        var views = GetViews();
        var n = views.RemoveAll(x => string.Equals(x.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (n == 0) return false;
        SetSetting(SavedViewsSetting, JsonSerializer.Serialize(views));
        return true;
    }
}
