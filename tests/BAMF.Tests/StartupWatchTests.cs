using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace BAMF.Tests;

/// <summary>The restart notice: a start after a clean stop says nothing, a start after being cut off says how long BAMF was blind, and it can be switched off.</summary>
[Collection("Endpoints")]
public class StartupWatchTests : IDisposable
{
    private static readonly DateTime T = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    // The tests that count starts and stops use a store of their own. A host the test server starts has a StartupWatch of its own,
    // which writes "alive" in the background, and sharing its store let that land between a test's own writes.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bamf-startup-" + Guid.NewGuid().ToString("N"));

    public StartupWatchTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private StartupWatch Own()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bamf:DatabasePath"] = Path.Combine(_dir, "own.db"),
        }).Build();
        return new StartupWatch(new HostStore(config), null, NullLogger<StartupWatch>.Instance);
    }

    private static StartupWatch Watch(BamfApp app) => new(app.Services.GetRequiredService<HostStore>(), null, NullLogger<StartupWatch>.Instance);

    [Fact]
    public void A_first_ever_start_has_nothing_to_report()
    {
        Assert.Null(Own().Begin(T));
    }

    [Fact]
    public void A_start_after_a_normal_stop_says_nothing_so_updates_stay_quiet()
    {
        var w = Own();
        w.Begin(T);
        w.Alive(T.AddMinutes(5));
        w.Stopped(T.AddMinutes(6));
        Assert.Null(w.Begin(T.AddMinutes(9)));
    }

    [Fact]
    public void A_start_after_being_cut_off_reports_when_BAMF_was_last_running()
    {
        var w = Own();
        w.Begin(T);
        w.Alive(T.AddMinutes(5));                // ...and then the power went
        var cut = w.Begin(T.AddMinutes(17));
        Assert.Equal(T.AddMinutes(5), cut);
    }

    [Fact]
    public void Being_cut_off_twice_in_a_row_is_reported_twice()
    {
        var w = Own();
        w.Begin(T);
        Assert.NotNull(w.Begin(T.AddMinutes(3)));
        Assert.NotNull(w.Begin(T.AddMinutes(6)));
        w.Stopped(T.AddMinutes(7));
        Assert.Null(w.Begin(T.AddMinutes(8)));
    }

    [Fact]
    public void The_message_says_how_long_and_that_a_normal_stop_doesnt_count()
    {
        var (title, detail) = StartupWatch.Message(T, T.AddMinutes(12));
        Assert.Contains("unexpectedly", title);
        Assert.Contains("about 12 minutes", detail);
        Assert.Contains("updating doesn't", detail);
        Assert.Contains("about 1 minute ", StartupWatch.Message(T, T.AddSeconds(20)).Detail);
    }

    [Fact]
    public async Task It_is_on_until_switched_off_and_the_dashboard_is_told()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var store = app.Services.GetRequiredService<HostStore>();
        Assert.True(Watch(app).Enabled);
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/hosts")).GetProperty("restartAlertEnabled").GetBoolean());
        Assert.True((await c.PostAsJsonAsync("/api/settings/restart-alert", new { enabled = false })).IsSuccessStatusCode);
        Assert.False(Watch(app).Enabled);
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/hosts")).GetProperty("restartAlertEnabled").GetBoolean());
        Assert.Equal("Alert when BAMF restarts unexpectedly", SettingsLog.Label("POST", "/api/settings/restart-alert"));
        store.SetSetting("restartAlert", "true");
        Assert.True(Watch(app).Enabled);
    }

    [Fact]
    public void A_normal_stop_of_the_host_is_written_down_as_clean()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        app.Services.GetServices<IHostedService>().OfType<StartupWatch>().Single();      // it's one of the host's own services
        for (var i = 0; i < 100 && string.IsNullOrEmpty(store.GetSetting("aliveAt")); i++) Thread.Sleep(50);      // its own start has been noted
        Assert.Equal("false", store.GetSetting("cleanStop"));
        app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();    // what the service manager's stop and Ctrl+C both end in
        Assert.Equal("true", store.GetSetting("cleanStop"));
    }

    [Fact]
    public async Task What_it_writes_down_never_travels_in_a_settings_file()
    {
        using var app = new BamfApp();
        Watch(app).Begin(T);
        var export = await app.Client().GetStringAsync("/api/settings/export");
        Assert.DoesNotContain("aliveAt", export);
        Assert.DoesNotContain("cleanStop", export);
    }
}
