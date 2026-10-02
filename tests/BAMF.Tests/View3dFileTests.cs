using System.Net;

namespace BAMF.Tests;

/// <summary>
/// The 3D tab's library and models are plain files BAMF serves: a browser only runs a module script that
/// comes back as JavaScript, and only reads a model that comes back as one, so the types are checked.
/// </summary>
[Collection("Endpoints")]
public class View3dFileTests
{
    [Theory]
    [InlineData("/view3d/scene.mjs", "text/javascript")]
    [InlineData("/view3d/data.mjs", "text/javascript")]
    [InlineData("/view3d/three/three.module.min.js", "text/javascript")]
    [InlineData("/view3d/models/router.glb", "model/gltf-binary")]
    [InlineData("/view3d/models/device.glb", "model/gltf-binary")]
    public async Task The_3D_files_are_served_with_the_type_a_browser_needs(string path, string type)
    {
        using var app = new BamfApp();
        var r = await app.Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(type, r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_3D_files_are_not_held_back_after_an_update()
    {
        using var app = new BamfApp();
        var r = await app.Client().GetAsync("/view3d/scene.mjs");
        Assert.Contains("no-cache", r.Headers.CacheControl?.ToString() ?? "");
    }
}
