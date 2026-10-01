using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>Unusual activity: what's been noticed, "that's normal", and switching it on and off.</summary>
internal static class UnusualEndpoints
{
    public static void Map(WebApplication app)
    {
        // What's open now, and anything from the last day; with how many
        // devices are watched and how many are still learning their normal.
        app.MapGet("/api/unusual", (HostStore store, UnusualWatch watch) =>
        {
            var (watching, learning) = watch.Coverage(DateTime.UtcNow);
            var mutes = store.UnusualMutes();
            return Results.Json(new
            {
                enabled = watch.Enabled, watching, learning, learnDays = Unusual.LearnDays,
                items = store.RecentUnusual(24).Select(u => new
                {
                    u.Id, u.HostId, u.Kind, u.At, u.Title, u.Detail, u.ResolvedAt, open = u.ResolvedAt is null,
                    normal = mutes.Contains((u.HostId, u.Kind)),
                }),
            });
        });

        // "That's normal": this kind of thing isn't raised for this device for
        // 30 days, and this one is closed.
        app.MapPost("/api/unusual/{id:long}/normal", (long id, HostStore store) =>
        {
            if (store.GetUnusual(id) is not { } u) return Results.NotFound(new { error = "That's no longer here." });
            store.MuteUnusual(u.HostId, u.Kind, UnusualWatch.MuteFor);
            store.ResolveUnusual(u.Id);
            return Results.Json(new { ok = true, mutedDays = (int)UnusualWatch.MuteFor.TotalDays });
        });

        app.MapPost("/api/settings/unusual", (UnusualSettingRequest body, HostStore store) =>
        {
            store.SetSetting("unusualWatch", body.Enabled ? "true" : "false");
            return Results.Json(new { enabled = body.Enabled });
        });
    }
}
