using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LanWatch.Services;

/// <summary>A second theme in the same folder, sharing its files: Goat Night beside Goat.</summary>
public record ThemeVariant(string Id, string Name, string[] Swatch, string? Description);

/// <summary>A theme folder, as its theme.json describes it.</summary>
public record DropInTheme(string Id, string Name, string[] Swatch, bool HasCss, bool HasJs,
    string? Category, string? Description, string? Author, bool Secret, string? Preview, List<ThemeVariant> Variants);

/// <summary>
/// Themes as folders: each folder under the themes directory (by default
/// &lt;install&gt;/themes) that holds a theme.json is a theme, with an optional
/// theme.css, theme.js and preview picture beside it. Adding a folder adds a
/// theme to the menu and deleting it removes it; nothing needs rebuilding.
///
/// Only those files are ever served, and only from folders with a plain
/// lower-case name, so nothing else on disk is reachable through here. A theme
/// runs its script in the dashboard, which is why they live on the server,
/// where adding one takes the same access as editing appsettings.json.
/// </summary>
public static partial class DropInThemes
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,39}$")]
    internal static partial Regex IdPattern();

    [GeneratedRegex(@"^#[0-9a-fA-F]{3,8}$")]
    private static partial Regex ColourPattern();

    internal static readonly Dictionary<string, string> Files = new()
    {
        ["theme.json"] = "application/json; charset=utf-8",
        ["theme.css"] = "text/css; charset=utf-8",
        ["theme.js"] = "text/javascript; charset=utf-8",
        ["preview.webp"] = "image/webp",
        ["preview.png"] = "image/png",
        ["preview.jpg"] = "image/jpeg",
    };
    internal static readonly string[] Categories = ["colours", "animated", "holiday"];

    public static List<DropInTheme> List(string dir)
    {
        var themes = new List<DropInTheme>();
        if (!Directory.Exists(dir)) return themes;
        var seen = new HashSet<string>();
        foreach (var folder in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var t = Read(folder);
            if (t is null || !seen.Add(t.Id)) continue;
            // A variant can't take a name another theme already has.
            t.Variants.RemoveAll(v => !seen.Add(v.Id));
            themes.Add(t);
        }
        return themes;
    }

    /// <summary>One theme folder, or null if it isn't one.</summary>
    public static DropInTheme? Read(string folder)
    {
        var id = Path.GetFileName(folder);
        if (!IdPattern().IsMatch(id)) return null;
        var manifest = Path.Combine(folder, "theme.json");
        if (!File.Exists(manifest)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var name = Text(root, "name", 40) ?? id;
            var category = Text(root, "category", 20)?.ToLowerInvariant();
            if (category is not null && !Categories.Contains(category)) category = null;
            var variants = new List<ThemeVariant>();
            if (root.TryGetProperty("variants", out var vs) && vs.ValueKind == JsonValueKind.Array)
                foreach (var v in vs.EnumerateArray().Take(8))
                {
                    if (v.ValueKind != JsonValueKind.Object) continue;
                    var vid = Text(v, "id", 40);
                    if (vid is null || !IdPattern().IsMatch(vid) || vid == id || variants.Any(x => x.Id == vid)) continue;
                    variants.Add(new ThemeVariant(vid, Text(v, "name", 40) ?? vid, Swatch(v), Text(v, "description", 300)));
                }
            var preview = new[] { "preview.webp", "preview.png", "preview.jpg" }.FirstOrDefault(f => File.Exists(Path.Combine(folder, f)));
            return new DropInTheme(id, name, Swatch(root),
                File.Exists(Path.Combine(folder, "theme.css")), File.Exists(Path.Combine(folder, "theme.js")),
                category, Text(root, "description", 300), Text(root, "author", 60),
                root.TryGetProperty("secret", out var sec) && sec.ValueKind == JsonValueKind.True, preview, variants);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken theme.json leaves that theme out; the others still load.
            return null;
        }
    }

    private static string? Text(JsonElement e, string key, int max)
    {
        if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString()!.Trim();
        return s.Length == 0 ? null : s.Length > max ? s[..max] : s;
    }

    private static string[] Swatch(JsonElement e) =>
        e.TryGetProperty("swatch", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!)
                .Where(c => ColourPattern().IsMatch(c)).Take(3).ToArray()
            : [];

    /// <summary>
    /// A theme's file, or null if that isn't something served. The id can be a
    /// variant's, which is served from the folder it lives in.
    /// </summary>
    public static (string Path, string ContentType)? Resolve(string dir, string id, string file)
    {
        if (!IdPattern().IsMatch(id) || !Files.TryGetValue(file, out var type)) return null;
        var path = Path.Combine(dir, id, file);
        if (File.Exists(path)) return (path, type);
        var owner = List(dir).FirstOrDefault(t => t.Variants.Any(v => v.Id == id));
        if (owner is null) return null;
        path = Path.Combine(dir, owner.Id, file);
        return File.Exists(path) ? (path, type) : null;
    }
}

/// <summary>
/// The themes that come with BAMF, and the themes folder they're installed into.
///
/// BAMF ships its themes in &lt;install&gt;/theme-library, which every update
/// replaces. At startup each one is copied into the themes folder unless it
/// has been there before: so a new install gets them all, an update adds any
/// new ones, and one you removed stays removed. A library theme still exactly
/// as it was installed is brought up to date with the library; one you've
/// edited is left alone. .library.json in the themes folder remembers what was
/// offered and what it looked like.
///
/// A .zip dropped into the themes folder is unpacked into it on the next look,
/// one theme or a pack of them, so installing a theme someone shares is a
/// single file.
/// </summary>
public sealed class ThemeLibrary
{
    private const string StateFile = ".library.json";
    private const long MaxZip = 10 * 1024 * 1024, MaxUnpacked = 25 * 1024 * 1024;
    private readonly object _lock = new();
    private readonly ILogger _log;
    private readonly List<(string File, string Problem, DateTime At)> _problems = [];

    public string ThemesDir { get; }
    public string LibraryDir { get; }

    public ThemeLibrary(string themesDir, string libraryDir, ILogger log)
    {
        ThemesDir = themesDir;
        LibraryDir = libraryDir;
        _log = log;
    }

    public IReadOnlyList<(string File, string Problem, DateTime At)> Problems { get { lock (_lock) return _problems.ToList(); } }

    private Dictionary<string, string> LoadState()
    {
        try
        {
            var p = Path.Combine(ThemesDir, StateFile);
            if (File.Exists(p))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(p));
                if (doc.RootElement.TryGetProperty("offered", out var o) && o.ValueKind == JsonValueKind.Object)
                    return o.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.String)
                        .ToDictionary(x => x.Name, x => x.Value.GetString()!);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Couldn't read {File}; the library themes will be offered again: {Error}", StateFile, ex.Message);
        }
        return [];
    }

    private void SaveState(Dictionary<string, string> offered)
    {
        var p = Path.Combine(ThemesDir, StateFile);
        File.WriteAllText(p, JsonSerializer.Serialize(new { offered = new SortedDictionary<string, string>(offered, StringComparer.Ordinal) },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>A folder's theme files, fingerprinted, so an edit to any of them shows.</summary>
    private static string Hash(string folder)
    {
        using var sha = SHA256.Create();
        foreach (var f in DropInThemes.Files.Keys.Order(StringComparer.Ordinal))
        {
            var p = Path.Combine(folder, f);
            if (!File.Exists(p)) continue;
            var name = Encoding.UTF8.GetBytes(f + "\0");
            sha.TransformBlock(name, 0, name.Length, null, 0);
            var bytes = File.ReadAllBytes(p);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>
    /// Makes a theme folder's files the same as another's, one file at a time,
    /// in place. 1.52.0 built a fresh copy beside it and swapped the folders,
    /// and on Windows the swap can fail: a folder just deleted can linger for
    /// a moment while something (the antivirus, the indexer) still has it
    /// open, so the rename onto its name is refused. Writing the files over
    /// never renames a folder, and a file that's briefly held is tried again.
    /// </summary>
    internal static void CopyTheme(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in DropInThemes.Files.Keys)
        {
            var src = Path.Combine(from, f);
            var dst = Path.Combine(to, f);
            if (File.Exists(src)) Retry(() => File.Copy(src, dst, true));
            else if (File.Exists(dst)) Retry(() => File.Delete(dst));
        }
    }

    private static void Retry(Action act)
    {
        for (var i = 0; ; i++)
        {
            try { act(); return; }
            catch (Exception ex) when (i < 5 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(200 * (i + 1)); }
        }
    }

    /// <summary>
    /// A theme 1.52.0 left half-swapped: its new copy stranded as &lt;id&gt;.new
    /// beside an emptied folder. Put the copy back in its place, or clear the
    /// leftover if the theme is there after all.
    /// </summary>
    private void Recover()
    {
        foreach (var tmp in Directory.GetDirectories(ThemesDir, "*.new"))
        {
            var to = tmp[..^4];
            try
            {
                if (File.Exists(Path.Combine(tmp, "theme.json")) && !File.Exists(Path.Combine(to, "theme.json")))
                {
                    CopyTheme(tmp, to);
                    _log.LogInformation("Themes: put {Id} back from the copy an earlier update left beside it", Path.GetFileName(to));
                }
                Directory.Delete(tmp, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("Themes: couldn't tidy {Dir}: {Error}", tmp, ex.Message);
            }
        }
    }

    public List<DropInTheme> Library() => DropInThemes.List(LibraryDir);

    /// <summary>Brings the themes folder up to date with the library. Run at startup.</summary>
    public void Sync()
    {
        lock (_lock)
        {
            Dictionary<string, string> offered;
            try
            {
                Directory.CreateDirectory(ThemesDir);
                Recover();
                offered = LoadState();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("Couldn't bring the themes in {Dir} up to date with the library: {Error}", ThemesDir, ex.Message);
                return;
            }
            int added = 0, updated = 0;
            // One theme that can't be written doesn't stop the rest.
            foreach (var t in Library())
            {
                var from = Path.Combine(LibraryDir, t.Id);
                var to = Path.Combine(ThemesDir, t.Id);
                try
                {
                    var fresh = Hash(from);
                    if (Directory.Exists(to))
                    {
                        var now = Hash(to);
                        // The same as the library's copy: it's the library's, whatever was recorded.
                        if (now == fresh) offered[t.Id] = fresh;
                        else if (offered.TryGetValue(t.Id, out var was) && was == now)
                        {
                            CopyTheme(from, to);
                            offered[t.Id] = fresh;
                            updated++;
                        }
                    }
                    else if (!offered.ContainsKey(t.Id))
                    {
                        CopyTheme(from, to);
                        offered[t.Id] = fresh;
                        added++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning("Themes: couldn't update {Id} from the library: {Error}", t.Id, ex.Message);
                }
            }
            try { SaveState(offered); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("Themes: couldn't save {File}: {Error}", StateFile, ex.Message);
            }
            if (added + updated > 0)
                _log.LogInformation("Themes: {Added} added and {Updated} updated from the library in {Dir}", added, updated, ThemesDir);
        }
    }

    /// <summary>Puts a library theme (back) into the themes folder.</summary>
    public string? Install(string id)
    {
        lock (_lock)
        {
            var t = Library().FirstOrDefault(x => x.Id == id);
            if (t is null) return "There's no theme called that in the library.";
            try
            {
                Directory.CreateDirectory(ThemesDir);
                var from = Path.Combine(LibraryDir, id);
                CopyTheme(from, Path.Combine(ThemesDir, id));
                var offered = LoadState();
                offered[id] = Hash(from);
                SaveState(offered);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "It couldn't be copied into the themes folder: " + ex.Message; }
            _log.LogInformation("Themes: {Id} installed from the library", id);
            return null;
        }
    }

    /// <summary>
    /// Takes a theme out of the themes folder. A library theme is deleted, since
    /// the library still has it; anything else is moved into themes/.removed, so
    /// a theme that exists nowhere else isn't lost to a stray click.
    /// </summary>
    public string? Remove(string id)
    {
        lock (_lock)
        {
            if (!DropInThemes.IdPattern().IsMatch(id)) return "That isn't a theme's name.";
            var folder = Path.Combine(ThemesDir, id);
            if (!Directory.Exists(folder)) return "That theme isn't installed.";
            var offered = LoadState();
            var fromLibrary = Directory.Exists(Path.Combine(LibraryDir, id)) && offered.TryGetValue(id, out var was) && was == Hash(folder);
            try
            {
                if (fromLibrary) Directory.Delete(folder, true);
                else
                {
                    // Kept, not deleted: copied into .removed, then taken out of the folder.
                    var bin = Path.Combine(ThemesDir, ".removed", $"{id}-{DateTime.Now:yyyyMMdd-HHmmss}");
                    CopyTheme(folder, bin);
                    Directory.Delete(folder, true);
                }
                // Remembered as offered, so the next start doesn't put it back.
                if (Directory.Exists(Path.Combine(LibraryDir, id)) && !offered.ContainsKey(id)) offered[id] = "";
                SaveState(offered);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "It couldn't be taken out of the themes folder: " + ex.Message; }
            _log.LogInformation("Themes: {Id} removed{Where}", id, fromLibrary ? "" : " (moved to themes/.removed)");
            return null;
        }
    }

    /// <summary>Whether an installed theme is the library's, untouched.</summary>
    public Dictionary<string, bool> FromLibrary()
    {
        lock (_lock)
        {
            var offered = LoadState();
            var result = new Dictionary<string, bool>();
            foreach (var t in DropInThemes.List(ThemesDir))
                result[t.Id] = Directory.Exists(Path.Combine(LibraryDir, t.Id)) && offered.TryGetValue(t.Id, out var was)
                    && was == Hash(Path.Combine(ThemesDir, t.Id));
            return result;
        }
    }

    /// <summary>Unpacks any .zip in the themes folder. Cheap when there are none.</summary>
    public void ImportZips()
    {
        if (!Directory.Exists(ThemesDir)) return;
        foreach (var zip in Directory.GetFiles(ThemesDir, "*.zip"))
        {
            var (ids, problem) = ImportZip(zip, Path.GetFileNameWithoutExtension(zip));
            try
            {
                if (problem is null) File.Delete(zip);
                else File.Move(zip, zip + ".failed", true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (problem is not null) Problem(Path.GetFileName(zip), problem);
            else _log.LogInformation("Themes: installed {Ids} from {Zip}", string.Join(", ", ids), Path.GetFileName(zip));
        }
    }

    private void Problem(string file, string problem)
    {
        _log.LogWarning("Themes: {File} wasn't installed: {Problem}", file, problem);
        lock (_lock)
        {
            _problems.RemoveAll(p => p.File == file);
            _problems.Add((file, problem, DateTime.UtcNow));
            if (_problems.Count > 20) _problems.RemoveAt(0);
        }
    }

    /// <summary>
    /// Installs the theme or themes in a zip: every folder in it with a
    /// theme.json, or the zip itself if theme.json is at its top, named after
    /// the zip. Only a theme's own files are taken out.
    /// </summary>
    public (List<string> Ids, string? Problem) ImportZip(string path, string fallbackId)
    {
        try
        {
            if (new FileInfo(path).Length > MaxZip) return ([], "it's over 10 MB; a theme is a few small files.");
            using var z = ZipFile.OpenRead(path);
            if (z.Entries.Count > 2000) return ([], "it has too many files in it to be a theme.");
            // Nothing that climbs out of the zip or starts from a drive or the root.
            var manifests = z.Entries.Where(e => e.Name.Equals("theme.json", StringComparison.OrdinalIgnoreCase)
                && !e.FullName.Split('/', '\\').Contains("..") && !e.FullName.StartsWith('/') && !e.FullName.StartsWith('\\') && !e.FullName.Contains(':')).ToList();
            if (manifests.Count == 0) return ([], "there's no theme.json in it.");
            var found = new List<(string Id, string Prefix)>();
            foreach (var m in manifests)
            {
                var prefix = m.FullName[..^m.Name.Length];
                var parts = prefix.TrimEnd('/', '\\').Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
                var id = (parts.Length > 0 ? parts[^1] : fallbackId).ToLowerInvariant();
                if (!DropInThemes.IdPattern().IsMatch(id)) return ([], $"\"{id}\" can't be a theme's name: use lower-case letters, digits and dashes.");
                if (found.Any(f => f.Id == id)) continue;
                found.Add((id, prefix));
            }
            long total = 0;
            var ids = new List<string>();
            lock (_lock)
            {
                Directory.CreateDirectory(ThemesDir);
                foreach (var (id, prefix) in found)
                {
                    var tmp = Path.Combine(ThemesDir, $".import-{id}");
                    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                    Directory.CreateDirectory(tmp);
                    foreach (var e in z.Entries)
                    {
                        if (!e.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        var rest = e.FullName[prefix.Length..];
                        var key = DropInThemes.Files.Keys.FirstOrDefault(k => k.Equals(rest, StringComparison.OrdinalIgnoreCase));
                        if (key is null) continue;
                        total += e.Length;
                        if (total > MaxUnpacked) { Directory.Delete(tmp, true); return (ids, "it unpacks to more than 25 MB."); }
                        e.ExtractToFile(Path.Combine(tmp, key), true);
                    }
                    if (!ValidManifest(tmp))
                    {
                        Directory.Delete(tmp, true);
                        return (ids, $"{id}'s theme.json can't be read.");
                    }
                    CopyTheme(tmp, Path.Combine(ThemesDir, id));
                    try { Directory.Delete(tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    ids.Add(id);
                }
            }
            return (ids, null);
        }
        catch (InvalidDataException) { return ([], "it isn't a zip file BAMF can open."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ([], ex.Message); }
    }

    private static bool ValidManifest(string folder)
    {
        try { using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "theme.json"))); return doc.RootElement.ValueKind == JsonValueKind.Object; }
        catch (Exception ex) when (ex is JsonException or IOException) { return false; }
    }
}
