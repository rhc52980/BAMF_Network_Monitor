using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BAMF.Tests;

/// <summary>The count on the Activity tab: what is new since a time, not counting wake notes, and the server's own clock is what "seen" is set to.</summary>
[Collection("Endpoints")]
public class UnseenTests
{
    private static async Task<JsonElement> Unseen(HttpClient c, string? since = null) =>
        await c.GetFromJsonAsync<JsonElement>("/api/activity/unseen" + (since is null ? "" : "?since=" + Uri.EscapeDataString(since)));

    [Fact]
    public async Task Without_a_time_it_counts_nothing_and_says_what_time_it_is_for_the_dashboard_to_keep()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        store.AddAlert("security", "Something", "detail");
        var j = await Unseen(app.Client());
        Assert.Equal(0, j.GetProperty("alerts").GetInt32());
        Assert.Equal(0, j.GetProperty("problems").GetInt32());
        Assert.InRange((DateTime.UtcNow - DateTime.Parse(j.GetProperty("now").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()).TotalSeconds, -5, 5);
        Assert.Equal(0, (await Unseen(app.Client(), "not a time")).GetProperty("alerts").GetInt32());
    }

    [Fact]
    public async Task Alerts_after_the_time_count_and_wake_notes_dont()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var c = app.Client();
        store.AddAlert("security", "Before", "old");
        await Task.Delay(30);
        var since = (await Unseen(c)).GetProperty("now").GetString()!;
        await Task.Delay(30);
        store.AddAlert("rule", "The NAS was offline", "x");
        store.AddAlert("port", "A port opened", "x");
        store.AddAlert("wake", "Woke the NAS", "x");
        Assert.Equal(2, (await Unseen(c, since)).GetProperty("alerts").GetInt32());
        var later = (await Unseen(c)).GetProperty("now").GetString()!;
        Assert.Equal(0, (await Unseen(c, later)).GetProperty("alerts").GetInt32());
    }

    [Fact]
    public async Task Problems_that_happened_after_the_time_count()
    {
        using var app = new BamfApp();
        var problems = app.Services.GetRequiredService<ProblemLog>();
        var c = app.Client();
        problems.Add("Old", "warning", "before");
        await Task.Delay(30);
        var since = (await Unseen(c)).GetProperty("now").GetString()!;
        await Task.Delay(30);
        problems.Add("RemoteService", "warning", "Remote Cabin: HTTP 401");
        problems.Add("Old", "warning", "before");        // the same old one again: it happened again, so it is new
        Assert.Equal(2, (await Unseen(c, since)).GetProperty("problems").GetInt32());
    }
}
