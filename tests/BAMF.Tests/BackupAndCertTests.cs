using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>
/// Nightly backups and the alert before BAMF's own certificate runs out, in
/// BAMF itself, running in memory on a database of its own.
/// </summary>
[Collection("Endpoints")]
public class BackupAndCertTests
{
    private static async Task<JsonElement> Backup(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("editable").GetProperty("backup");

    [Fact]
    public async Task Nightly_backups_are_on_by_default_at_3_keeping_7()
    {
        using var app = new BamfApp();
        var b = await Backup(app.Client());
        Assert.True(b.GetProperty("enabled").GetBoolean());
        Assert.Equal(3, b.GetProperty("hour").GetInt32());
        Assert.Equal(7, b.GetProperty("keep").GetInt32());
        Assert.Equal(0, b.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Back_up_now_writes_tonights_backup_as_a_sound_database()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var r = await c.PostAsync("/api/settings/backup/run", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var file = Path.Combine(app.Dir, "backups", $"nightly-{DateTime.Now:yyyyMMdd}.db");
        Assert.True(File.Exists(file));
        Assert.Equal("SQLite format 3", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(file), 0, 15));
        // Nothing half-written left beside it; appsettings.json beside it, when there is one.
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(app.Dir, "backups")), f => Path.GetFileName(f).StartsWith("bamf-backup-"));
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "appsettings.json")))
            Assert.True(File.Exists(Path.ChangeExtension(file, ".appsettings.json")));
        var b = await Backup(c);
        Assert.Equal(1, b.GetProperty("count").GetInt32());
        Assert.Equal(Path.GetFileName(file), b.GetProperty("last").GetProperty("file").GetString());
    }

    [Fact]
    public async Task Keeping_fewer_deletes_the_oldest_nightly_backups_and_nothing_else()
    {
        using var app = new BamfApp();
        var dir = Path.Combine(app.Dir, "backups");
        Directory.CreateDirectory(dir);
        foreach (var d in new[] { "20260101", "20260102", "20260103", "20260104" }) File.WriteAllText(Path.Combine(dir, $"nightly-{d}.db"), "");
        File.WriteAllText(Path.Combine(dir, "bamf-20260101-0300.db"), "");   // an updater's: not ours to prune
        var r = await app.Client().PostAsJsonAsync("/api/settings/backup", new { keep = 2 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(["bamf-20260101-0300.db", "nightly-20260103.db", "nightly-20260104.db"],
            Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).Order().ToArray());
    }

    [Fact]
    public async Task A_second_folder_set_in_the_file_gets_a_copy()
    {
        var other = Path.Combine(Path.GetTempPath(), "bamf-copyto-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var app = new BamfApp(settings: new() { ["Bamf:Backup:CopyTo"] = other });
            var c = app.Client();
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/settings/backup/run", null)).StatusCode);
            Assert.True(File.Exists(Path.Combine(other, $"nightly-{DateTime.Now:yyyyMMdd}.db")));
            Assert.Equal(other, (await Backup(c)).GetProperty("copyTo").GetString());
        }
        finally { try { Directory.Delete(other, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData(2, true, 3, false, false)]    // before the hour
    [InlineData(3, true, 3, false, true)]     // at it
    [InlineData(9, true, 3, false, true)]     // off at 3, back at 9: catches up
    [InlineData(9, true, 3, true, false)]     // already taken today
    [InlineData(9, false, 3, false, false)]   // switched off
    public void A_backup_is_due_from_the_hour_on_once_a_day(int nowHour, bool enabled, int hour, bool taken, bool due)
    {
        Assert.Equal(due, NightlyBackup.Due(new DateTime(2026, 9, 30, nowHour, 15, 0), enabled, hour, taken));
    }

    [Fact]
    public async Task Keeping_fewer_takes_each_backups_appsettings_copy_with_it()
    {
        using var app = new BamfApp();
        var dir = Path.Combine(app.Dir, "backups");
        Directory.CreateDirectory(dir);
        foreach (var d in new[] { "20260101", "20260102" })
        {
            File.WriteAllText(Path.Combine(dir, $"nightly-{d}.db"), "");
            File.WriteAllText(Path.Combine(dir, $"nightly-{d}.appsettings.json"), "{}");
        }
        await app.Client().PostAsJsonAsync("/api/settings/backup", new { keep = 1 });
        Assert.Equal(["nightly-20260102.appsettings.json", "nightly-20260102.db"],
            Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).Order().ToArray());
    }

    [Fact]
    public async Task A_second_folder_set_in_Settings_gets_a_copy()
    {
        var other = Path.Combine(Path.GetTempPath(), "bamf-copyto-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var app = new BamfApp();
            var c = app.Client();
            var r = await c.PostAsJsonAsync("/api/settings/backup", new { copyTo = other });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.False(File.Exists(Path.Combine(other, ".bamf-write-test")));   // the check cleans up after itself
            await c.PostAsync("/api/settings/backup/run", null);
            Assert.True(File.Exists(Path.Combine(other, $"nightly-{DateTime.Now:yyyyMMdd}.db")));
            var b = await Backup(c);
            Assert.Equal((other, "settings"), (b.GetProperty("copyTo").GetString(), b.GetProperty("copyToSource").GetString()));
        }
        finally { try { Directory.Delete(other, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task A_second_folder_must_be_a_full_path_BAMF_can_write_to()
    {
        using var app = new BamfApp();
        var c = app.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/settings/backup", new { copyTo = "backups/copy" })).StatusCode);
        // A folder under a file can never be made.
        var file = Path.Combine(app.Dir, "a-file");
        File.WriteAllText(file, "");
        var r = await c.PostAsJsonAsync("/api/settings/backup", new { copyTo = Path.Combine(file, "sub") });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("can't write there", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, (await Backup(c)).GetProperty("copyTo").ValueKind);
    }

    [Fact]
    public async Task Blank_in_Settings_means_no_copy_even_with_one_in_the_file_and_reset_hands_it_back()
    {
        var other = Path.Combine(Path.GetTempPath(), "bamf-copyto-" + Guid.NewGuid().ToString("N"));
        using var app = new BamfApp(settings: new() { ["Bamf:Backup:CopyTo"] = other });
        var c = app.Client();
        Assert.Equal("file", (await Backup(c)).GetProperty("copyToSource").GetString());
        await c.PostAsJsonAsync("/api/settings/backup", new { copyTo = "" });
        var off = await Backup(c);
        Assert.Equal((JsonValueKind.Null, "settings"), (off.GetProperty("copyTo").ValueKind, off.GetProperty("copyToSource").GetString()));
        await c.PostAsync("/api/settings/backup/copyto/reset", null);
        var back = await Backup(c);
        Assert.Equal((other, "file"), (back.GetProperty("copyTo").GetString(), back.GetProperty("copyToSource").GetString()));
        var log = await c.GetFromJsonAsync<JsonElement>("/api/settings/log");
        Assert.Equal("Nightly backups' second copy: back to appsettings.json", log[0].GetProperty("what").GetString());
    }

    [Theory]
    [InlineData(24, 7)]
    [InlineData(3, 0)]
    [InlineData(3, 61)]
    public async Task A_bad_hour_or_count_is_refused(int hour, int keep)
    {
        using var app = new BamfApp();
        var r = await app.Client().PostAsJsonAsync("/api/settings/backup", new { hour, keep });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Changing_the_schedule_is_in_the_settings_log()
    {
        using var app = new BamfApp();
        var c = app.Client();
        await c.PostAsJsonAsync("/api/settings/backup", new { enabled = false });
        var log = await c.GetFromJsonAsync<JsonElement>("/api/settings/log");
        Assert.Equal("Nightly backups", log[0].GetProperty("what").GetString());
        Assert.False((await Backup(c)).GetProperty("enabled").GetBoolean());
    }

    // ---------- restoring a backup kept on this machine ----------

    [Fact]
    public async Task A_backup_on_this_machine_can_be_put_back_by_name()
    {
        using var app = new BamfApp();
        var c = app.Client();
        await c.PostAsJsonAsync("/api/settings/newdays", new { days = 3 });
        await c.PostAsync("/api/settings/backup/run", null);
        await c.PostAsJsonAsync("/api/settings/newdays", new { days = 9 });
        Assert.Equal(9, (await c.GetFromJsonAsync<JsonElement>("/api/hosts")).GetProperty("newDays").GetInt32());

        var list = await c.GetFromJsonAsync<JsonElement>("/api/backup/saved");
        var nightly = list.EnumerateArray().Single(b => b.GetProperty("kind").GetString() == "nightly");
        var r = await c.PostAsJsonAsync("/api/backup/saved/restore", new { name = nightly.GetProperty("name").GetString() });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        // Back as it was then, and what it replaced is in the list to undo it.
        Assert.Equal(3, (await c.GetFromJsonAsync<JsonElement>("/api/hosts")).GetProperty("newDays").GetInt32());
        var after = await c.GetFromJsonAsync<JsonElement>("/api/backup/saved");
        Assert.Contains(after.EnumerateArray(), b => b.GetProperty("kind").GetString() == "before a restore");
    }

    [Fact]
    public async Task The_list_says_what_each_backup_is()
    {
        using var app = new BamfApp();
        var dir = Path.Combine(app.Dir, "backups");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bamf-20260930-1705.db"), "");
        File.WriteAllText(Path.Combine(dir, "bamf-backup-0123abcd.db"), "");   // one being written: not listed
        await app.Client().PostAsync("/api/settings/backup/run", null);
        var kinds = (await app.Client().GetFromJsonAsync<JsonElement>("/api/backup/saved")).EnumerateArray()
            .Select(b => (b.GetProperty("name").GetString(), b.GetProperty("kind").GetString())).ToList();
        Assert.Contains(($"nightly-{DateTime.Now:yyyyMMdd}.db", "nightly"), kinds);
        Assert.Contains(("bamf-20260930-1705.db", "before an update"), kinds);
        Assert.DoesNotContain(kinds, k => k.Item1!.StartsWith("bamf-backup-"));
    }

    [Theory]
    [InlineData("../bamf.db")]
    [InlineData(@"..\bamf.db")]
    [InlineData("sub/nightly-20260101.db")]
    [InlineData(@"C:\Windows\win.db")]
    [InlineData("nightly-19990101.db")]   // well-formed, but not there
    [InlineData("bamf.db.txt")]
    public async Task Only_a_backup_in_the_backups_folder_can_be_restored(string name)
    {
        using var app = new BamfApp();
        var r = await app.Client().PostAsJsonAsync("/api/backup/saved/restore", new { name });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task The_view_only_password_can_neither_list_nor_restore()
    {
        using var app = new BamfApp();
        var c = app.Client();
        await c.PostAsJsonAsync("/api/settings/password", new { role = "admin", password = "main-test-pass-1" });
        var viewer = new HttpRequestMessage(HttpMethod.Post, "/api/settings/password") { Content = JsonContent.Create(new { role = "viewer", current = "main-test-pass-1", password = "viewer-test-pass-1" }) };
        viewer.Headers.Authorization = BamfApp.Basic("main-test-pass-1");
        await c.SendAsync(viewer);
        var v = app.Client();
        var list = new HttpRequestMessage(HttpMethod.Get, "/api/backup/saved");
        list.Headers.Authorization = BamfApp.Basic("viewer-test-pass-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await v.SendAsync(list)).StatusCode);
        var put = new HttpRequestMessage(HttpMethod.Post, "/api/backup/saved/restore") { Content = JsonContent.Create(new { name = "nightly-20260101.db" }) };
        put.Headers.Authorization = BamfApp.Basic("viewer-test-pass-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await v.SendAsync(put)).StatusCode);
    }

    // ---------- BAMF's own certificate ----------

    [Fact]
    public async Task A_certificate_near_its_end_gets_one_alert_per_stage()
    {
        using var app = new BamfApp();
        var c = app.Client();
        // Made 815 days ago, so 10 of its 825 days are left.
        var (names, addresses) = HttpsCert.LocalNames();
        using (var cert = HttpsCert.Create(names, addresses, DateTimeOffset.UtcNow.AddDays(-815)))
            HttpsCert.Save(cert, Path.Combine(app.Dir, HttpsCert.FileName));

        var security = app.Services.GetRequiredService<SecurityCheck>();
        await security.CheckOwnCert(CancellationToken.None);
        await security.CheckOwnCert(CancellationToken.None);

        var alerts = await c.GetFromJsonAsync<JsonElement>("/api/alerts");
        var mine = alerts.EnumerateArray().Where(a => a.GetProperty("title").GetString()!.StartsWith("BAMF's HTTPS certificate")).ToList();
        var only = Assert.Single(mine);
        Assert.Equal("BAMF's HTTPS certificate expires in 10 days", only.GetProperty("title").GetString());
        Assert.Contains("Settings → Security → HTTPS", only.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task No_certificate_no_alert()
    {
        using var app = new BamfApp();
        await app.Services.GetRequiredService<SecurityCheck>().CheckOwnCert(CancellationToken.None);
        var alerts = await app.Client().GetFromJsonAsync<JsonElement>("/api/alerts");
        Assert.DoesNotContain(alerts.EnumerateArray(), a => a.GetProperty("title").GetString()!.StartsWith("BAMF's HTTPS certificate"));
    }
}
