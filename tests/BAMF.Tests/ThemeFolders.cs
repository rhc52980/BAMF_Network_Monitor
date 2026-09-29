using System.IO.Compression;
using System.Text;
using LanWatch.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BAMF.Tests;

/// <summary>
/// A throwaway library folder and themes folder for one test, deleted after.
/// </summary>
public sealed class ThemeFolders : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "bamf-tests-" + Guid.NewGuid().ToString("N"));
    public string Library => Path.Combine(Root, "theme-library");
    public string Themes => Path.Combine(Root, "themes");

    public ThemeFolders()
    {
        Directory.CreateDirectory(Library);
    }

    public ThemeLibrary NewLibrary() => new(Themes, Library, NullLogger.Instance);

    /// <summary>A theme folder with a theme.json, and a stylesheet and script unless told otherwise.</summary>
    public static void WriteTheme(string folder, string name, string css = "body{}", string? js = "BAMF.registerTheme('x', () => {});",
        string? json = null)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "theme.json"), json ?? $$"""{ "name": "{{name}}", "category": "animated", "swatch": ["#112233", "#445566"] }""");
        if (css is not null) File.WriteAllText(Path.Combine(folder, "theme.css"), css);
        if (js is not null) File.WriteAllText(Path.Combine(folder, "theme.js"), js);
    }

    public void LibraryTheme(string id, string css = "body{}", string? js = "BAMF.registerTheme('x', () => {});") =>
        WriteTheme(Path.Combine(Library, id), id, css, js);

    public string Installed(string id, string file = "theme.css") => File.ReadAllText(Path.Combine(Themes, id, file));

    /// <summary>A zip with the given entries, each a path inside the zip and its text.</summary>
    public string Zip(string name, params (string Path, string Text)[] entries)
    {
        var path = Path.Combine(Root, name);
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (p, text) in entries)
        {
            using var s = z.CreateEntry(p).Open();
            s.Write(Encoding.UTF8.GetBytes(text));
        }
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
