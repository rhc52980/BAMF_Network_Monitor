using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BAMF.Tests;

/// <summary>The Notes card on Activity: free text kept on the server, and two browsers can't quietly overwrite each other.</summary>
[Collection("Endpoints")]
public class NotesTests
{
    private static async Task<JsonElement> Get(HttpClient c) => await c.GetFromJsonAsync<JsonElement>("/api/notes");

    [Fact]
    public async Task Nothing_is_saved_until_something_is()
    {
        using var app = new BamfApp();
        var d = await Get(app.Client());
        Assert.Equal("", d.GetProperty("text").GetString());
        Assert.Equal("", d.GetProperty("updatedAt").GetString());
        Assert.Equal(20000, d.GetProperty("max").GetInt32());
    }

    [Fact]
    public async Task What_is_saved_comes_back_exactly_with_its_line_breaks_and_survives_a_restart()
    {
        using var app = new BamfApp();
        var c = app.Client();
        const string text = "Router password is on the fridge.\r\n\r\n  - move the NAS\n  - <b>not markup</b> ☃";
        var r = await c.PostAsJsonAsync("/api/notes", new { text, based = "" });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var d = await Get(c);
        Assert.Equal(text, d.GetProperty("text").GetString());
        Assert.NotEqual("", d.GetProperty("updatedAt").GetString());

        using var again = new BamfApp(app.Dir);
        Assert.Equal(text, (await Get(again.Client())).GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_save_from_a_page_that_has_missed_a_newer_one_is_refused_and_gets_the_saved_text()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var first = await (await c.PostAsJsonAsync("/api/notes", new { text = "one", based = "" })).Content.ReadFromJsonAsync<JsonElement>();
        var stamp = first.GetProperty("updatedAt").GetString();
        // another browser saves on top of that stamp
        var second = await (await c.PostAsJsonAsync("/api/notes", new { text = "two", based = stamp })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(stamp, second.GetProperty("updatedAt").GetString());

        // the first browser still holds the old stamp
        var stale = await c.PostAsJsonAsync("/api/notes", new { text = "three", based = stamp });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("two", body.GetProperty("text").GetString());
        Assert.Equal(second.GetProperty("updatedAt").GetString(), body.GetProperty("updatedAt").GetString());
        Assert.Equal("two", (await Get(c)).GetProperty("text").GetString());

        // keeping its own text means saving again with the stamp the refusal handed back
        var keep = await c.PostAsJsonAsync("/api/notes", new { text = "three", based = body.GetProperty("updatedAt").GetString() });
        Assert.True(keep.IsSuccessStatusCode);
        Assert.Equal("three", (await Get(c)).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Leaving_out_the_stamp_writes_whatever_is_there_and_empty_text_clears_it()
    {
        using var app = new BamfApp();
        var c = app.Client();
        await c.PostAsJsonAsync("/api/notes", new { text = "one" });
        await c.PostAsJsonAsync("/api/notes", new { text = "two" });
        Assert.Equal("two", (await Get(c)).GetProperty("text").GetString());
        var clear = await c.PostAsJsonAsync("/api/notes", new { text = "" });
        Assert.True(clear.IsSuccessStatusCode);
        Assert.Equal("", (await Get(c)).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Too_much_text_is_refused_and_the_old_notes_stay()
    {
        using var app = new BamfApp();
        var c = app.Client();
        await c.PostAsJsonAsync("/api/notes", new { text = "keep me" });
        var r = await c.PostAsJsonAsync("/api/notes", new { text = new string('x', 20001) });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("keep me", (await Get(c)).GetProperty("text").GetString());
        var edge = await c.PostAsJsonAsync("/api/notes", new { text = new string('x', 20000) });
        Assert.True(edge.IsSuccessStatusCode);
    }
}
