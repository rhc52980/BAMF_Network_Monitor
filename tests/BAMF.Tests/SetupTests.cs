using LanWatch.Services;
using Microsoft.Extensions.Configuration;

namespace BAMF.Tests;

/// <summary>
/// The first-run setup is offered to a new install only: one whose database
/// BAMF made at that start. An install updated from an older version, whose
/// database was already there, never sees it.
/// </summary>
public class SetupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bamf-setup-" + Guid.NewGuid().ToString("N"));

    public SetupTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private HostStore Store() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Bamf:DatabasePath"] = Path.Combine(_dir, "bamf.db"),
    }).Build());

    [Fact]
    public void A_new_install_is_offered_setup()
    {
        Assert.Equal("1", Store().GetSetting("setupPending"));
    }

    [Fact]
    public void Setup_stays_offered_until_its_done()
    {
        Store();
        Assert.Equal("1", Store().GetSetting("setupPending"));
    }

    [Fact]
    public void Once_its_done_it_isnt_offered_again()
    {
        Store().DeleteSetting("setupPending");
        Assert.Null(Store().GetSetting("setupPending"));
    }

    [Fact]
    public void An_install_from_before_setup_existed_isnt_offered_it()
    {
        // A database from an older BAMF: there, but with no setup flag at all.
        var s = Store();
        s.DeleteSetting("setupPending");
        s.SetSetting("scanIntervalSeconds", "60");
        Assert.Null(Store().GetSetting("setupPending"));
    }
}
