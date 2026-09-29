using LanWatch.Services;

namespace BAMF.Tests;

/// <summary>
/// Reading theme folders, and which of their files BAMF will serve. Only a
/// theme's own files, from a folder with a plain lower-case name.
/// </summary>
public class DropInThemeTests : IDisposable
{
    private readonly ThemeFolders _f = new();
    public void Dispose() => _f.Dispose();

    private string Folder(string id) => Path.Combine(_f.Themes, id);

    [Fact]
    public void A_folder_with_a_theme_json_is_a_theme()
    {
        ThemeFolders.WriteTheme(Folder("goat"), "Goat");
        var t = Assert.Single(DropInThemes.List(_f.Themes));
        Assert.Equal("goat", t.Id);
        Assert.Equal("Goat", t.Name);
        Assert.Equal("animated", t.Category);
        Assert.True(t.HasCss);
        Assert.True(t.HasJs);
    }

    [Theory]
    [InlineData("Goat")]
    [InlineData("goat night")]
    [InlineData("-goat")]
    [InlineData("goat_night")]
    public void A_folder_whose_name_isnt_plain_is_left_out(string name)
    {
        ThemeFolders.WriteTheme(Folder(name), "x");
        Assert.Empty(DropInThemes.List(_f.Themes));
    }

    [Fact]
    public void A_broken_theme_json_leaves_that_theme_out_and_no_other()
    {
        ThemeFolders.WriteTheme(Folder("goat"), "Goat");
        ThemeFolders.WriteTheme(Folder("broken"), "x", json: "{ nope");
        Directory.CreateDirectory(Folder("empty"));
        Assert.Equal(["goat"], DropInThemes.List(_f.Themes).Select(t => t.Id));
    }

    [Fact]
    public void An_unknown_category_and_bad_colours_are_dropped()
    {
        ThemeFolders.WriteTheme(Folder("odd"), "x",
            json: """{ "name": "Odd", "category": "weird", "swatch": ["#abc", "red", "#11223344", "#fff", "#000"] }""");
        var t = Assert.Single(DropInThemes.List(_f.Themes));
        Assert.Null(t.Category);
        Assert.Equal(["#abc", "#11223344", "#fff"], t.Swatch);   // the first three good ones
    }

    [Fact]
    public void Variants_are_read_and_one_cant_take_another_themes_name()
    {
        ThemeFolders.WriteTheme(Folder("claw"), "x",
            json: """{ "name": "Claw", "variants": [ { "id": "clawdusk", "name": "Claw Dusk" }, { "id": "goat", "name": "Not goat" } ] }""");
        ThemeFolders.WriteTheme(Folder("goat"), "Goat");
        var claw = DropInThemes.List(_f.Themes).Single(t => t.Id == "claw");
        Assert.Equal(["clawdusk"], claw.Variants.Select(v => v.Id));
    }

    [Fact]
    public void A_themes_files_are_served()
    {
        ThemeFolders.WriteTheme(Folder("goat"), "Goat");
        var css = DropInThemes.Resolve(_f.Themes, "goat", "theme.css");
        Assert.NotNull(css);
        Assert.StartsWith("text/css", css!.Value.ContentType);
    }

    [Fact]
    public void A_variants_files_come_from_the_folder_it_lives_in()
    {
        ThemeFolders.WriteTheme(Folder("claw"), "x", json: """{ "name": "Claw", "variants": [ { "id": "clawdusk" } ] }""");
        var js = DropInThemes.Resolve(_f.Themes, "clawdusk", "theme.js");
        Assert.Equal(Path.Combine(Folder("claw"), "theme.js"), js?.Path);
    }

    [Theory]
    [InlineData("goat", "secret.txt")]
    [InlineData("goat", "../appsettings.json")]
    [InlineData("../goat", "theme.css")]
    [InlineData("..", "theme.json")]
    [InlineData("nope", "theme.css")]
    public void Nothing_but_a_themes_own_files_is_served(string id, string file)
    {
        ThemeFolders.WriteTheme(Folder("goat"), "Goat");
        File.WriteAllText(Path.Combine(Folder("goat"), "secret.txt"), "no");
        File.WriteAllText(Path.Combine(_f.Root, "theme.json"), "{}");
        Assert.Null(DropInThemes.Resolve(_f.Themes, id, file));
    }
}
