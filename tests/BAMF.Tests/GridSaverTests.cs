using System.Net;

namespace BAMF.Tests;

/// <summary>
/// The grid saver's library, modules and models are plain files BAMF serves: a browser only runs a module script that comes back as
/// JavaScript, and only reads a model that comes back as one, so the types are checked. The page offers the saver and says where its
/// library is, and the library is not fetched from anywhere else.
/// </summary>
[Collection("Endpoints")]
public class GridSaverTests
{
    [Theory]
    [InlineData("/engine3d/grid.mjs", "text/javascript")]
    [InlineData("/engine3d/scene.mjs", "text/javascript")]
    [InlineData("/engine3d/three/addons/controls/OrbitControls.js", "text/javascript")]
    [InlineData("/engine3d/data.mjs", "text/javascript")]
    [InlineData("/engine3d/fx.mjs", "text/javascript")]
    [InlineData("/engine3d/extras.mjs", "text/javascript")]
    [InlineData("/engine3d/house.mjs", "text/javascript")]
    [InlineData("/engine3d/sound.mjs", "text/javascript")]
    [InlineData("/engine3d/three/three.module.min.js", "text/javascript")]
    [InlineData("/engine3d/three/addons/loaders/GLTFLoader.js", "text/javascript")]
    [InlineData("/engine3d/models/router.glb", "model/gltf-binary")]
    [InlineData("/engine3d/models/device.glb", "model/gltf-binary")]
    public async Task The_saver_files_are_served_with_the_type_a_browser_needs(string path, string type)
    {
        using var app = new BamfApp();
        var r = await app.Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(type, r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_saver_modules_are_not_held_back_after_an_update()
    {
        using var app = new BamfApp();
        var r = await app.Client().GetAsync("/engine3d/grid.mjs");
        Assert.Contains("no-cache", r.Headers.CacheControl?.ToString() ?? "");
    }

    [Fact]
    public async Task The_page_offers_the_grid_and_keeps_its_library_local()
    {
        using var app = new BamfApp();
        var html = await app.Client().GetStringAsync("/");
        Assert.Contains("<script type=\"importmap\">", html);
        Assert.Contains("\"three\":\"/engine3d/three/three.module.min.js\"", html);       // not a CDN
        Assert.Contains("id=\"saverGrid\"", html);
        foreach (var style in new[] { "grid", "grid-terminal", "grid-siren" }) Assert.Contains($"<option value=\"{style}\">", html);
    }

    [Fact]
    public void Every_addon_the_grid_imports_is_in_the_folder()
    {
        // The module names its three.js add-ons by path; a missing one would fail only when the saver first started.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "BAMF", "wwwroot", "engine3d"));
        Assert.True(Directory.Exists(root), root);
        var imports = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(root, "grid.mjs")), "from \"three/addons/([^\"]+)\"");
        Assert.True(imports.Count >= 5);
        foreach (System.Text.RegularExpressions.Match m in imports)
        {
            var file = Path.Combine(root, "three", "addons", m.Groups[1].Value);
            Assert.True(File.Exists(file), file);
            // and what that file imports of its own is there too
            foreach (System.Text.RegularExpressions.Match inner in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "from '\\./([^']+)'"))
                Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(file)!, inner.Groups[1].Value)), inner.Groups[1].Value);
        }
    }
}
