namespace BAMF.Tests;

/// <summary>
/// Installing a theme someone shares, as a zip: from Settings, or dropped
/// into the themes folder. Only a theme's own files come out of it, and
/// nothing in it can reach outside the themes folder.
/// </summary>
public class ThemeZipTests : IDisposable
{
    private readonly ThemeFolders _f = new();
    public void Dispose() => _f.Dispose();

    private const string Json = """{ "name": "Sunset", "category": "colours" }""";

    [Fact]
    public void A_theme_at_the_top_of_the_zip_is_named_after_it()
    {
        var zip = _f.Zip("sunset.zip", ("theme.json", Json), ("theme.css", "body{}"));
        var (ids, problem) = _f.NewLibrary().ImportZip(zip, "sunset");
        Assert.Null(problem);
        Assert.Equal(["sunset"], ids);
        Assert.Equal("body{}", _f.Installed("sunset"));
    }

    [Fact]
    public void A_pack_installs_every_theme_in_it()
    {
        var zip = _f.Zip("pack.zip",
            ("dawn/theme.json", Json), ("dawn/theme.css", "a"),
            ("dusk/theme.json", Json), ("dusk/theme.js", "BAMF.registerTheme('dusk', () => {});"));
        var (ids, problem) = _f.NewLibrary().ImportZip(zip, "pack");
        Assert.Null(problem);
        Assert.Equal(["dawn", "dusk"], ids.Order());
    }

    [Fact]
    public void Only_a_themes_own_files_are_taken_out()
    {
        var zip = _f.Zip("sunset.zip", ("theme.json", Json), ("theme.css", "body{}"), ("run.exe", "MZ"), ("notes.txt", "hi"));
        _f.NewLibrary().ImportZip(zip, "sunset");
        Assert.Equal(["theme.css", "theme.json"], Directory.GetFiles(Path.Combine(_f.Themes, "sunset")).Select(Path.GetFileName).Order());
    }

    [Theory]
    [InlineData("../escape/theme.json")]
    [InlineData("a/../../escape/theme.json")]
    [InlineData("/escape/theme.json")]
    [InlineData("C:/escape/theme.json")]
    public void Nothing_in_a_zip_reaches_outside_the_themes_folder(string entry)
    {
        var zip = _f.Zip("evil.zip", (entry, Json));
        var (ids, problem) = _f.NewLibrary().ImportZip(zip, "evil");
        Assert.Empty(ids);
        Assert.NotNull(problem);
        Assert.False(Directory.Exists(Path.Combine(_f.Root, "escape")));
    }

    [Fact]
    public void A_name_that_isnt_a_themes_is_refused()
    {
        var zip = _f.Zip("pack.zip", ("My Theme/theme.json", Json));
        var (ids, problem) = _f.NewLibrary().ImportZip(zip, "pack");
        Assert.Empty(ids);
        Assert.Contains("can't be a theme's name", problem);
    }

    [Fact]
    public void A_broken_theme_json_is_refused_and_leaves_nothing_behind()
    {
        var zip = _f.Zip("sunset.zip", ("theme.json", "{ nope"), ("theme.css", "body{}"));
        var (ids, problem) = _f.NewLibrary().ImportZip(zip, "sunset");
        Assert.Empty(ids);
        Assert.Contains("can't be read", problem);
        Assert.Empty(Directory.GetDirectories(_f.Themes));
    }

    [Fact]
    public void A_zip_without_a_theme_is_refused()
    {
        var zip = _f.Zip("empty.zip", ("readme.txt", "hello"));
        Assert.Equal("there's no theme.json in it.", _f.NewLibrary().ImportZip(zip, "empty").Problem);
    }

    [Fact]
    public void A_file_that_isnt_a_zip_is_refused()
    {
        var path = Path.Combine(_f.Root, "fake.zip");
        File.WriteAllText(path, "not a zip at all");
        Assert.Equal("it isn't a zip file BAMF can open.", _f.NewLibrary().ImportZip(path, "fake").Problem);
    }

    [Fact]
    public void A_zip_dropped_into_the_themes_folder_is_installed_and_tidied_away()
    {
        Directory.CreateDirectory(_f.Themes);
        var zip = _f.Zip("sunset.zip", ("theme.json", Json), ("theme.css", "body{}"));
        File.Move(zip, Path.Combine(_f.Themes, "sunset.zip"));
        var lib = _f.NewLibrary();
        lib.ImportZips();
        Assert.True(File.Exists(Path.Combine(_f.Themes, "sunset", "theme.json")));
        Assert.False(File.Exists(Path.Combine(_f.Themes, "sunset.zip")));
        Assert.Empty(lib.Problems);
    }

    [Fact]
    public void A_dropped_zip_that_fails_is_set_aside_and_reported()
    {
        Directory.CreateDirectory(_f.Themes);
        File.WriteAllText(Path.Combine(_f.Themes, "broken.zip"), "not a zip");
        var lib = _f.NewLibrary();
        lib.ImportZips();
        Assert.True(File.Exists(Path.Combine(_f.Themes, "broken.zip.failed")));
        Assert.Contains(lib.Problems, p => p.File == "broken.zip");
    }
}
