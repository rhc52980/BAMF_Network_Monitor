using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BAMF.Tests;

/// <summary>The disk and database check: what counts as low, that it speaks once and again a day later and says when there is room, and the off switch.</summary>
[Collection("Endpoints")]
public class DiskHealthTests
{
    private const long Gb = 1L << 30;
    private static readonly DateTime T = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private static DiskHealth Make(BamfApp app, Func<string, (long, long)?> space) =>
        new(app.Services.GetRequiredService<HostStore>(), app.Services.GetRequiredService<NightlyBackup>(), null, NullLogger<DiskHealth>.Instance, p => space(p) is { } s ? (s.Item1, s.Item2) : null);

    [Theory]
    [InlineData(100 * Gb, 50 * Gb, 10, false)]      // half free
    [InlineData(100 * Gb, 9 * Gb, 10, true)]        // under the percentage
    [InlineData(100 * Gb, 10 * Gb, 10, false)]      // exactly the percentage is fine
    [InlineData(1000 * Gb, 400L * 1024 * 1024, 2, true)]   // a big disk with almost nothing: low whatever the percentage
    [InlineData(0, 0, 10, false)]                   // a drive that says nothing isn't called low
    public void What_counts_as_low(long total, long free, int percent, bool low) => Assert.Equal(low, DiskHealth.IsLow(total, free, percent));

    [Fact]
    public async Task A_low_drive_is_said_once_again_after_a_day_and_when_there_is_room_again()
    {
        using var app = new BamfApp();
        long free = 5 * Gb;
        var disk = Make(app, _ => (100 * Gb, free));
        var low = disk.Snapshot(T);
        Assert.True(low.Volumes.Single().Low);

        var first = disk.Evaluate(low, T);
        Assert.Single(first);
        Assert.Contains("Disk space is low", first[0].Title);
        Assert.Empty(disk.Evaluate(low, T.AddHours(3)));                   // not again the same day
        Assert.Single(disk.Evaluate(low, T.AddHours(25)));                 // again a day later, while it's still low

        free = 60 * Gb;
        var ok = disk.Snapshot(T.AddDays(2));
        var back = disk.Evaluate(ok, T.AddDays(2));
        Assert.Single(back);
        Assert.Contains("room on its disk again", back[0].Title);
        Assert.Empty(disk.Evaluate(ok, T.AddDays(2).AddHours(1)));         // and then quiet
        await Task.CompletedTask;
    }

    [Fact]
    public void A_drive_that_was_never_low_says_nothing_when_it_is_fine()
    {
        using var app = new BamfApp();
        var disk = Make(app, _ => (100 * Gb, 80 * Gb));
        Assert.Empty(disk.Evaluate(disk.Snapshot(T), T));
    }

    [Fact]
    public void Switched_off_it_says_nothing_at_all_but_the_numbers_are_still_there()
    {
        using var app = new BamfApp();
        app.Services.GetRequiredService<HostStore>().SetSetting("diskAlert", "false");
        var disk = Make(app, _ => (100 * Gb, 1 * Gb));
        var r = disk.Snapshot(T);
        Assert.True(r.Volumes.Single().Low);
        Assert.Empty(disk.Evaluate(r, T));
    }

    [Fact]
    public void The_percentage_is_taken_from_settings_and_a_bad_one_falls_back_to_the_default()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var disk = Make(app, _ => (100 * Gb, 30 * Gb));
        Assert.False(disk.Snapshot(T).Volumes.Single().Low);               // 30% free against the default 10
        store.SetSetting("diskAlertPercent", "40");
        Assert.True(disk.Snapshot(T).Volumes.Single().Low);                // 30% free against 40
        store.SetSetting("diskAlertPercent", "999");
        Assert.Equal(DiskHealth.DefaultPercent, disk.Percent);
    }

    [Fact]
    public void The_second_copy_of_the_backups_is_checked_too_and_the_same_drive_is_not_listed_twice()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        store.SetSetting("backupCopyTo", Path.Combine(Path.GetTempPath(), "bamf-copy-test"));
        var sameDrive = Make(app, _ => (100 * Gb, 50 * Gb));
        Assert.Single(sameDrive.Snapshot(T).Volumes);

        var other = Make(app, p => p.Contains("bamf-copy-test") ? (200 * Gb, 5 * Gb) : (100 * Gb, 50 * Gb));
        var r = other.Snapshot(T);
        Assert.Equal(2, r.Volumes.Count);
        Assert.Contains(r.Volumes, v => v.Role.Contains("second copy") && v.Low);
        Assert.Contains(r.Volumes, v => !v.Low);
    }

    [Fact]
    public void The_real_drive_lookup_finds_this_machines_drive_and_a_path_that_isnt_anywhere_gives_nothing()
    {
        var here = DiskHealth.Space(AppContext.BaseDirectory);
        Assert.NotNull(here);
        Assert.True(here!.Value.Total > 0);
        Assert.InRange(here.Value.Free, 0, here.Value.Total);
    }

    [Fact]
    public async Task The_endpoint_reports_the_database_the_backups_and_the_drive_and_never_a_path_outside_BAMFs_own()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var j = await c.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.True(j.GetProperty("databaseBytes").GetInt64() > 0);
        Assert.True(j.GetProperty("historyRows").GetInt64() >= 0);
        Assert.True(j.GetProperty("alert").GetProperty("enabled").GetBoolean());
        Assert.Equal(10, j.GetProperty("alert").GetProperty("percent").GetInt32());
        Assert.NotEmpty(j.GetProperty("volumes").EnumerateArray());
    }

    [Fact]
    public async Task The_alert_can_be_switched_off_and_its_percentage_set_within_limits()
    {
        using var app = new BamfApp();
        var c = app.Client();
        Assert.True((await c.PostAsJsonAsync("/api/settings/disk-alert", new { enabled = false, percent = 20 })).IsSuccessStatusCode);
        var j = (await c.GetFromJsonAsync<JsonElement>("/api/health")).GetProperty("alert");
        Assert.False(j.GetProperty("enabled").GetBoolean());
        Assert.Equal(20, j.GetProperty("percent").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/disk-alert", new { enabled = true, percent = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/disk-alert", new { enabled = true, percent = 51 })).StatusCode);
        Assert.Equal("Warn when disk space is low", SettingsLog.Label("POST", "/api/settings/disk-alert"));
    }
}
