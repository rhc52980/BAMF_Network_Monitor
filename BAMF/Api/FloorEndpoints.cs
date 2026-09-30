using System.Text;
using LanWatch.Services;
using static LanWatch.Api.ApiHelpers;

namespace LanWatch.Api;

/// <summary>Floor plans.</summary>
internal static class FloorEndpoints
{
    public static void Map(WebApplication app)
    {
        // Floor plans: an image per floor, uploaded as the raw request body, and
        // where each device sits on one. PNG, JPEG or WebP only, up to 12 MB: an SVG
        // could carry script, and these are served from this origin.
        app.MapGet("/api/floors", (HostStore store) => Results.Json(new
        {
            floors = store.GetFloors().Select(f => new { id = f.Id, name = f.Name, width = f.Width, height = f.Height, updated = f.Updated, kind = f.Mime == HostStore.PlanMime ? "plan" : "image" }),
            places = store.GetFloorPlaces().Select(p => new { hostId = p.HostId, floorId = p.FloorId, x = p.X, y = p.Y }),
        }));

        app.MapGet("/api/floors/{id:long}/image", (long id, HostStore store, HttpContext ctx) =>
        {
            if (store.GetFloorImage(id) is not { } img) return Results.NotFound();
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.Bytes(img.Data, img.Mime);
        });

        app.MapPost("/api/floors", async (string? name, int? width, int? height, HttpContext ctx, HostStore store) =>
        {
            var (mime, data, error) = await ReadFloorImage(ctx);
            if (error is not null) return Results.BadRequest(new { error });
            if (width is not (> 0 and <= 20000) || height is not (> 0 and <= 20000)) return Results.BadRequest(new { error = "The image's size didn't come through." });
            var clean = (name ?? "").Trim();
            var id = store.AddFloor(clean == "" ? "Floor" : clean.Length > 40 ? clean[..40] : clean, mime!, data!, width.Value, height.Value);
            return Results.Json(new { id });
        });

        app.MapPost("/api/floors/{id:long}", async (long id, string? name, int? width, int? height, HttpContext ctx, HostStore store) =>
        {
            // A new name (query string), a new image (body), or both.
            string? mime = null; byte[]? data = null;
            if ((ctx.Request.ContentLength ?? 0) > 0)
            {
                (mime, data, var error) = await ReadFloorImage(ctx);
                if (error is not null) return Results.BadRequest(new { error });
                if (width is not (> 0 and <= 20000) || height is not (> 0 and <= 20000)) return Results.BadRequest(new { error = "The image's size didn't come through." });
            }
            var clean = name?.Trim();
            if (clean is { Length: > 40 }) clean = clean[..40];
            return store.UpdateFloor(id, string.IsNullOrEmpty(clean) ? null : clean, mime, data, width ?? 0, height ?? 0) ? Results.Ok() : Results.NotFound();
        });

        app.MapDelete("/api/floors/{id:long}", (long id, HostStore store) => store.DeleteFloor(id) ? Results.Ok() : Results.NotFound());

        app.MapGet("/api/floors/{id:long}/plan", (long id, HostStore store) =>
        {
            if (store.GetFloorImage(id) is not { } f || f.Mime != HostStore.PlanMime) return Results.NotFound();
            return Results.Text(System.Text.Encoding.UTF8.GetString(f.Data), "application/json");
        });

        app.MapPost("/api/floors/plan", async (string? name, HttpContext ctx, HostStore store) =>
        {
            var (plan, error) = await ReadFloorPlan(ctx);
            if (error is not null) return Results.BadRequest(new { error });
            var clean = (name ?? "").Trim();
            var id = store.AddFloor(clean == "" ? "Floor" : clean.Length > 40 ? clean[..40] : clean,
                HostStore.PlanMime, System.Text.Encoding.UTF8.GetBytes(plan!.Json), plan.Width, plan.Height);
            return Results.Json(new { id });
        });

        app.MapPost("/api/floors/{id:long}/plan", async (long id, string? name, HttpContext ctx, HostStore store) =>
        {
            var (plan, error) = await ReadFloorPlan(ctx);
            if (error is not null) return Results.BadRequest(new { error });
            var clean = name?.Trim();
            if (clean is { Length: > 40 }) clean = clean[..40];
            return store.UpdateFloor(id, string.IsNullOrEmpty(clean) ? null : clean, HostStore.PlanMime,
                System.Text.Encoding.UTF8.GetBytes(plan!.Json), plan.Width, plan.Height) ? Results.Ok() : Results.NotFound();
        });

        app.MapPost("/api/floors/{id:long}/places", (long id, FloorPlaceRequest body, HostStore store) =>
            store.PlaceOnFloor(body.HostId, id, body.X, body.Y) ? Results.Ok() : Results.NotFound());

        app.MapDelete("/api/floors/places/{hostId:long}", (long hostId, HostStore store) =>
        {
            store.RemoveFromFloor(hostId);
            return Results.Ok();
        });
    }
}
