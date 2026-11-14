using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>The Notes card on Activity: one block of free-form text kept on the server, so every browser sees the same.</summary>
internal static class NotesEndpoints
{
    public const int MaxLength = 20000;
    private static readonly object Gate = new();

    private static (string Text, string Stamp) Read(HostStore store) => (store.GetSetting("activityNotes") ?? "", store.GetSetting("activityNotesAt") ?? "");

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/notes", (HostStore store) =>
        {
            var (text, stamp) = Read(store);
            return Results.Json(new { text, updatedAt = stamp, max = MaxLength });
        });

        // `based` is the stamp the page last saw. If someone saved since, nothing is written and the saved text comes back (409), so two
        // browsers never quietly overwrite each other. Leave it out to write whatever the state is.
        app.MapPost("/api/notes", (NotesRequest body, HostStore store) =>
        {
            var text = body.Text ?? "";
            if (text.Length > MaxLength) return Results.BadRequest(new { error = $"Notes can be {MaxLength:N0} characters at most." });
            lock (Gate)
            {
                var (was, stamp) = Read(store);
                if (body.Based is not null && body.Based != stamp)
                    return Results.Json(new { error = "The notes were saved from somewhere else since you opened them.", text = was, updatedAt = stamp }, statusCode: 409);
                var now = DateTime.UtcNow.ToString("o");
                store.SetSetting("activityNotes", text);
                store.SetSetting("activityNotesAt", now);
                return Results.Json(new { text, updatedAt = now, max = MaxLength });
            }
        });
    }
}

internal sealed record NotesRequest(string? Text, string? Based);
