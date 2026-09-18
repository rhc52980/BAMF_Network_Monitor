using System.Text.Json;
using System.Text.RegularExpressions;

namespace LanWatch.Services;

/// <summary>A theme installed as its own folder, as the theme menu lists it.</summary>
public record DropInTheme(string Id, string Name, string[] Swatch, bool HasCss, bool HasJs);

/// <summary>
/// Drop-in themes: each folder under the themes directory (by default
/// &lt;install&gt;/themes) that holds a theme.json is a theme, with an optional
/// theme.css and theme.js beside it. Adding a folder adds a theme to the menu
/// and deleting it removes it; nothing needs rebuilding. Updates publish over
/// the install folder without touching it.
///
/// Only those three files are ever served, and only from folders with a plain
/// lower-case name, so nothing else on disk is reachable through here. A theme
/// runs its script in the dashboard, which is why they live on the server,
/// where adding one takes the same access as editing appsettings.json.
/// </summary>
public static partial class DropInThemes
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^#[0-9a-fA-F]{3,8}$")]
    private static partial Regex ColourPattern();

    private static readonly Dictionary<string, string> Files = new()
    {
        ["theme.css"] = "text/css; charset=utf-8",
        ["theme.js"] = "text/javascript; charset=utf-8",
    };

    public static List<DropInTheme> List(string dir)
    {
        var themes = new List<DropInTheme>();
        if (!Directory.Exists(dir)) return themes;
        foreach (var folder in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(folder);
            if (!IdPattern().IsMatch(id)) continue;
            var manifest = Path.Combine(folder, "theme.json");
            if (!File.Exists(manifest)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                var root = doc.RootElement;
                var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : id;
                if (name.Length == 0 || name.Length > 40) name = id;
                var swatch = root.TryGetProperty("swatch", out var s) && s.ValueKind == JsonValueKind.Array
                    ? s.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!)
                        .Where(c => ColourPattern().IsMatch(c)).Take(3).ToArray()
                    : Array.Empty<string>();
                themes.Add(new DropInTheme(id, name, swatch,
                    File.Exists(Path.Combine(folder, "theme.css")), File.Exists(Path.Combine(folder, "theme.js"))));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A broken theme.json leaves that theme out; the others still load.
            }
        }
        return themes;
    }

    /// <summary>A theme's css or js file, or null if that isn't something served.</summary>
    public static (string Path, string ContentType)? Resolve(string dir, string id, string file)
    {
        if (!IdPattern().IsMatch(id) || !Files.TryGetValue(file, out var type)) return null;
        var path = Path.Combine(dir, id, file);
        return File.Exists(path) ? (path, type) : null;
    }
}
