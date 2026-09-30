using System.Text.RegularExpressions;
using LanWatch.Services;
using static LanWatch.Api.ApiHelpers;

namespace LanWatch.Api;

/// <summary>Themes: the installed ones, the library, Holiday Spirit and Night mode.</summary>
internal static class ThemeEndpoints
{
    public static void Map(WebApplication app, string themesDir, ThemeLibrary themeLib)
    {
        // The installed themes, one entry per theme a menu can offer: a folder's
        // variants (Goat Night beside Goat) are listed as themes of their own. A .zip
        // dropped into the folder is unpacked first, so it shows on the next reload.
        app.MapGet("/api/themes", () =>
        {
            themeLib.ImportZips();
            return Results.Json(DropInThemes.List(themesDir).SelectMany(t =>
                new[] { new { id = t.Id, name = t.Name, swatch = t.Swatch, description = t.Description } }
                    .Concat(t.Variants.Select(v => new { id = v.Id, name = v.Name, swatch = v.Swatch.Length > 0 ? v.Swatch : t.Swatch, description = v.Description ?? t.Description }))
                    .Select(x => new
                    {
                        x.id, folder = t.Id, x.name, x.swatch, x.description, category = t.Category, author = t.Author,
                        secret = t.Secret, css = t.HasCss, js = t.HasJs,
                    })));
        });

        // Settings → Appearance → Themes: every theme in the library and every one
        // installed, whether it's in the folder, and anything that failed to install.
        app.MapGet("/api/themes/library", () =>
        {
            themeLib.ImportZips();
            var installed = DropInThemes.List(themesDir);
            var library = themeLib.Library();
            var stock = themeLib.FromLibrary();
            var ids = library.Select(t => t.Id).Union(installed.Select(t => t.Id)).ToList();
            return Results.Json(new
            {
                folder = themesDir,
                themes = ids.Select(id =>
                {
                    var inst = installed.FirstOrDefault(t => t.Id == id);
                    var lib = library.FirstOrDefault(t => t.Id == id);
                    var t = inst ?? lib!;
                    var preview = inst?.Preview is { } p ? $"/themes/{id}/{p}" : lib?.Preview is { } q ? $"/theme-library/{id}/{q}" : null;
                    return new
                    {
                        id, name = t.Name, swatch = t.Swatch, category = t.Category, description = t.Description, author = t.Author,
                        secret = t.Secret, variants = t.Variants.Select(v => new { id = v.Id, name = v.Name }),
                        preview, installed = inst is not null, library = lib is not null, stock = stock.GetValueOrDefault(id),
                    };
                }),
                problems = themeLib.Problems.Select(p => new { file = p.File, problem = p.Problem, at = p.At }),
            });
        });

        app.MapPost("/api/themes/{id}/install", (string id) =>
            themeLib.Install(id) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok(new { ok = true }));

        app.MapPost("/api/themes/{id}/remove", (string id) =>
            themeLib.Remove(id) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok(new { ok = true }));

        // A theme someone shared, as a .zip, from the browser: the same as dropping it
        // into the themes folder.
        app.MapPost("/api/themes/upload", async (HttpContext ctx) =>
        {
            if (ctx.Request.ContentLength > 10 * 1024 * 1024) return Results.BadRequest(new { error = "That's over 10 MB; a theme is a few small files." });
            Directory.CreateDirectory(themesDir);
            var tmp = Path.Combine(themesDir, $".upload-{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var f = File.Create(tmp))
                {
                    var buf = new byte[81920];
                    long total = 0;
                    int n;
                    while ((n = await ctx.Request.Body.ReadAsync(buf)) > 0)
                    {
                        total += n;
                        if (total > 10 * 1024 * 1024) return Results.BadRequest(new { error = "That's over 10 MB; a theme is a few small files." });
                        await f.WriteAsync(buf.AsMemory(0, n));
                    }
                }
                var name = ctx.Request.Query["name"].ToString();
                var fallback = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
                var (ids, problem) = themeLib.ImportZip(tmp, fallback.Length > 0 ? fallback : "theme");
                return problem is null ? Results.Ok(new { ids }) : Results.BadRequest(new { error = "That zip wasn't installed: " + problem });
            }
            finally { try { File.Delete(tmp); } catch (IOException) { } }
        });

        app.MapGet("/themes/{id}/{file}", (string id, string file, HttpContext ctx) =>
        {
            var hit = DropInThemes.Resolve(themesDir, id, file);
            if (hit is null) return Results.NotFound();
            // Edits to a theme should show on the next reload, not after a cache expires.
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.File(hit.Value.Path, hit.Value.ContentType);
        });

        // A library theme's picture, for the list of themes that aren't installed.
        app.MapGet("/theme-library/{id}/{file}", (string id, string file) =>
        {
            if (!file.StartsWith("preview.", StringComparison.Ordinal)) return Results.NotFound();
            var hit = DropInThemes.Resolve(themeLib.LibraryDir, id, file);
            return hit is null ? Results.NotFound() : Results.File(hit.Value.Path, hit.Value.ContentType);
        });

        app.MapPost("/api/settings/holiday-spirit", (ActiveArpRequest body, HostStore store) =>
        {
            store.SetSetting("holidaySpirit", body.Enabled ? "true" : "false");
            return Results.Ok();
        });

        app.MapPost("/api/settings/night", (NightRequest body, HostStore store) =>
        {
            var clock = new System.Text.RegularExpressions.Regex(@"^([01]\d|2[0-3]):[0-5]\d$");
            var from = body.From ?? "21:00";
            var to = body.To ?? "06:00";
            var theme = (body.Theme ?? "nightstreet").Trim().ToLowerInvariant();
            if (!clock.IsMatch(from) || !clock.IsMatch(to))
                return Results.BadRequest(new { error = "Times must be HH:MM, 24-hour." });
            if (from == to)
                return Results.BadRequest(new { error = "Night has to start and end at different times." });
            if (!System.Text.RegularExpressions.Regex.IsMatch(theme, "^[a-z0-9-]{1,40}$"))
                return Results.BadRequest(new { error = "That isn't a theme id." });
            store.SetSetting("nightMode", body.Enabled ? "true" : "false");
            store.SetSetting("nightFrom", from);
            store.SetSetting("nightTo", to);
            store.SetSetting("nightTheme", theme);
            return Results.Ok();
        });
    }
}
