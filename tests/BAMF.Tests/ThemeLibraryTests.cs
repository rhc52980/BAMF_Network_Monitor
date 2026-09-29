namespace BAMF.Tests;

/// <summary>
/// The themes BAMF comes with, and what happens to them across updates: a new
/// install gets them all, an update brings untouched ones up to date and adds
/// new ones, and a theme you edited or removed is left as you left it.
/// </summary>
public class ThemeLibraryTests : IDisposable
{
    private readonly ThemeFolders _f = new();
    public void Dispose() => _f.Dispose();

    [Fact]
    public void A_new_install_gets_every_library_theme()
    {
        _f.LibraryTheme("goat");
        _f.LibraryTheme("harbour");
        _f.NewLibrary().Sync();
        Assert.True(File.Exists(Path.Combine(_f.Themes, "goat", "theme.json")));
        Assert.True(File.Exists(Path.Combine(_f.Themes, "harbour", "theme.js")));
        Assert.True(File.Exists(Path.Combine(_f.Themes, ".library.json")));
    }

    [Fact]
    public void An_update_brings_an_untouched_theme_up_to_date()
    {
        _f.LibraryTheme("goat", css: "old");
        _f.NewLibrary().Sync();
        _f.LibraryTheme("goat", css: "new");
        _f.NewLibrary().Sync();
        Assert.Equal("new", _f.Installed("goat"));
    }

    [Fact]
    public void An_update_leaves_a_theme_you_edited_alone()
    {
        _f.LibraryTheme("goat", css: "old");
        _f.NewLibrary().Sync();
        File.WriteAllText(Path.Combine(_f.Themes, "goat", "theme.css"), "mine");
        _f.LibraryTheme("goat", css: "new");
        _f.NewLibrary().Sync();
        Assert.Equal("mine", _f.Installed("goat"));
    }

    [Fact]
    public void An_update_drops_a_file_the_theme_no_longer_has()
    {
        _f.LibraryTheme("goat");
        _f.NewLibrary().Sync();
        File.Delete(Path.Combine(_f.Library, "goat", "theme.js"));
        _f.NewLibrary().Sync();
        Assert.False(File.Exists(Path.Combine(_f.Themes, "goat", "theme.js")));
    }

    [Fact]
    public void A_theme_new_in_an_update_is_added()
    {
        _f.LibraryTheme("goat");
        _f.NewLibrary().Sync();
        _f.LibraryTheme("matrix");
        _f.NewLibrary().Sync();
        Assert.True(Directory.Exists(Path.Combine(_f.Themes, "matrix")));
    }

    [Fact]
    public void A_removed_library_theme_stays_removed_through_updates()
    {
        _f.LibraryTheme("goat");
        var lib = _f.NewLibrary();
        lib.Sync();
        Assert.Null(lib.Remove("goat"));
        Assert.False(Directory.Exists(Path.Combine(_f.Themes, "goat")));
        _f.LibraryTheme("goat", css: "a newer version");
        _f.NewLibrary().Sync();
        Assert.False(Directory.Exists(Path.Combine(_f.Themes, "goat")));
    }

    [Fact]
    public void Install_puts_a_removed_theme_back_and_it_stays()
    {
        _f.LibraryTheme("goat");
        var lib = _f.NewLibrary();
        lib.Sync();
        lib.Remove("goat");
        Assert.Null(lib.Install("goat"));
        _f.NewLibrary().Sync();
        Assert.True(File.Exists(Path.Combine(_f.Themes, "goat", "theme.json")));
        Assert.True(_f.NewLibrary().FromLibrary()["goat"]);
    }

    [Fact]
    public void Install_refuses_a_theme_the_library_doesnt_have()
    {
        Assert.NotNull(_f.NewLibrary().Install("nope"));
    }

    [Fact]
    public void Removing_a_theme_of_your_own_keeps_a_copy()
    {
        ThemeFolders.WriteTheme(Path.Combine(_f.Themes, "mine"), "Mine", css: "only copy");
        Assert.Null(_f.NewLibrary().Remove("mine"));
        Assert.False(Directory.Exists(Path.Combine(_f.Themes, "mine")));
        var kept = Directory.GetDirectories(Path.Combine(_f.Themes, ".removed"), "mine-*");
        Assert.Single(kept);
        Assert.Equal("only copy", File.ReadAllText(Path.Combine(kept[0], "theme.css")));
    }

    [Fact]
    public void Removing_an_edited_library_theme_keeps_a_copy()
    {
        _f.LibraryTheme("goat");
        var lib = _f.NewLibrary();
        lib.Sync();
        File.WriteAllText(Path.Combine(_f.Themes, "goat", "theme.css"), "my edit");
        lib.Remove("goat");
        Assert.Single(Directory.GetDirectories(Path.Combine(_f.Themes, ".removed"), "goat-*"));
    }

    [Theory]
    [InlineData("../goat")]
    [InlineData("Goat")]
    [InlineData("")]
    public void Remove_refuses_a_name_that_isnt_a_themes(string id)
    {
        Assert.NotNull(_f.NewLibrary().Remove(id));
    }

    [Fact]
    public void Remove_says_so_when_the_theme_isnt_there()
    {
        Directory.CreateDirectory(_f.Themes);
        Assert.NotNull(_f.NewLibrary().Remove("goat"));
    }

    [Fact]
    public void FromLibrary_tells_an_untouched_theme_from_an_edited_one()
    {
        _f.LibraryTheme("goat");
        _f.LibraryTheme("matrix");
        var lib = _f.NewLibrary();
        lib.Sync();
        File.WriteAllText(Path.Combine(_f.Themes, "matrix", "theme.js"), "BAMF.registerTheme('matrix', () => {}); // mine");
        var from = lib.FromLibrary();
        Assert.True(from["goat"]);
        Assert.False(from["matrix"]);
    }

    [Fact]
    public void A_theme_an_earlier_update_left_half_swapped_is_put_back()
    {
        // 1.52.0 could strand the new copy as goat.new beside an emptied goat.
        _f.LibraryTheme("goat");
        ThemeFolders.WriteTheme(Path.Combine(_f.Themes, "goat.new"), "goat", css: "stranded");
        Directory.CreateDirectory(Path.Combine(_f.Themes, "goat"));
        _f.NewLibrary().Sync();
        Assert.True(File.Exists(Path.Combine(_f.Themes, "goat", "theme.json")));
        Assert.False(Directory.Exists(Path.Combine(_f.Themes, "goat.new")));
    }

    [Fact]
    public void A_damaged_record_of_what_was_offered_doesnt_stop_the_themes()
    {
        Directory.CreateDirectory(_f.Themes);
        File.WriteAllText(Path.Combine(_f.Themes, ".library.json"), "{ not json");
        _f.LibraryTheme("goat");
        _f.NewLibrary().Sync();
        Assert.True(File.Exists(Path.Combine(_f.Themes, "goat", "theme.json")));
    }
}
