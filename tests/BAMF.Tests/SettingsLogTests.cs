using LanWatch.Services;
using Microsoft.Extensions.Configuration;

namespace BAMF.Tests;

/// <summary>
/// The log of settings changes: which requests count as one and what they're
/// called, and that what's kept is the name alone.
/// </summary>
public class SettingsLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bamf-setlog-" + Guid.NewGuid().ToString("N"));

    public SettingsLogTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("POST", "/api/settings/mqtt", "Home Assistant (MQTT)")]
    [InlineData("POST", "/api/settings/mqtt/reset", "Home Assistant (MQTT): back to appsettings.json")]
    [InlineData("POST", "/api/settings/networks", "Networks")]
    [InlineData("POST", "/api/settings/password", "A password")]
    [InlineData("POST", "/api/settings/webhook", "Main webhook")]
    [InlineData("POST", "/API/Settings/Scan/", "Scanning")]
    [InlineData("POST", "/api/setup", "First-run setup")]
    [InlineData("POST", "/api/backup/restore", "Restored from a backup")]
    [InlineData("POST", "/api/themes/harbour/install", "Theme added: harbour")]
    [InlineData("POST", "/api/themes/harbour/remove", "Theme removed: harbour")]
    public void A_settings_change_is_named(string method, string path, string label)
    {
        Assert.Equal(label, SettingsLog.Label(method, path));
    }

    [Theory]
    [InlineData("GET", "/api/settings/mqtt")]        // reading isn't changing
    [InlineData("GET", "/api/settings/log")]
    [InlineData("POST", "/api/hosts/5/name")]         // a device's name is the device's history, not a setting
    [InlineData("POST", "/api/scan")]
    [InlineData("POST", "/api/signin")]
    [InlineData("POST", "/api/settings/something-new")]
    public void Anything_else_isnt_logged(string method, string path)
    {
        Assert.Null(SettingsLog.Label(method, path));
    }

    [Theory]
    [InlineData("admin", true, "the main password")]
    [InlineData("viewer", true, "the view-only password")]
    [InlineData(null, false, "no password set")]
    public void Who_is_by_password_not_by_name(string? role, bool passwordSet, string who)
    {
        Assert.Equal(who, SettingsLog.Who(role, passwordSet));
    }

    [Fact]
    public void Changes_are_kept_newest_first()
    {
        var store = new HostStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bamf:DatabasePath"] = Path.Combine(_dir, "bamf.db"),
        }).Build());
        store.LogSettingsChange("Networks", "the main password", "192.168.30.5");
        store.LogSettingsChange("Quiet hours", "the main password", "192.168.30.5");
        var log = store.GetSettingsLog();
        Assert.Equal(["Quiet hours", "Networks"], log.Select(c => c.What));
        Assert.Equal("192.168.30.5", log[0].Address);
    }
}
