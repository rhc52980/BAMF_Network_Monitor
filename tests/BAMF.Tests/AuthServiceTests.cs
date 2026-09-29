using LanWatch.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BAMF.Tests;

/// <summary>
/// Who may open BAMF: the passwords, where they come from, sessions, and
/// locking out an address that keeps guessing.
/// </summary>
public class AuthServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bamf-auth-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    public AuthServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A BAMF with its own database, and these appsettings.json passwords.</summary>
    private AuthService Bamf(string? password = null, string? viewer = null, string db = "bamf.db")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bamf:DatabasePath"] = Path.Combine(_dir, db),
            ["Bamf:Password"] = password,
            ["Bamf:ViewerPassword"] = viewer,
        }).Build();
        return new AuthService(new HostStore(config), config, NullLogger<AuthService>.Instance) { Now = () => _now };
    }

    [Fact]
    public void With_no_password_BAMF_is_open()
    {
        var a = Bamf();
        Assert.False(a.Required);
        Assert.Null(a.RoleFor("anything"));
        Assert.Null(a.Source("admin"));
    }

    [Fact]
    public void A_password_from_appsettings_still_works()
    {
        var a = Bamf("file-password", "file-viewer");
        Assert.True(a.Required);
        Assert.Equal("file", a.Source("admin"));
        Assert.Equal("admin", a.RoleFor("file-password"));
        Assert.Equal("viewer", a.RoleFor("file-viewer"));
        Assert.Null(a.RoleFor("wrong"));
    }

    [Fact]
    public void A_view_only_password_on_its_own_does_nothing()
    {
        var a = Bamf(viewer: "file-viewer");
        Assert.False(a.Required);
        Assert.Null(a.Source("viewer"));
        Assert.Null(a.RoleFor("file-viewer"));
    }

    [Fact]
    public void A_password_set_in_Settings_replaces_the_files()
    {
        var a = Bamf("file-password");
        Assert.Null(a.SetPassword("admin", "file-password", "settings-password"));
        Assert.Equal("settings", a.Source("admin"));
        Assert.Equal("admin", a.RoleFor("settings-password"));
        Assert.Null(a.RoleFor("file-password"));
    }

    [Fact]
    public void Setting_the_first_password_needs_nothing_else()
    {
        var a = Bamf();
        Assert.Null(a.SetPassword("admin", null, "first-password"));
        Assert.True(a.Required);
    }

    [Fact]
    public void Changing_a_password_takes_the_current_main_one()
    {
        var a = Bamf();
        a.SetPassword("admin", null, "first-password");
        Assert.Equal("The current password isn't right.", a.SetPassword("admin", "guess", "second-password"));
        Assert.Equal("The current password isn't right.", a.SetPassword("viewer", null, "viewer-password"));
        Assert.Null(a.SetPassword("admin", "first-password", "second-password"));
    }

    [Fact]
    public void A_short_password_is_refused()
    {
        Assert.Equal($"Use at least {AuthService.MinLength} characters.", Bamf().SetPassword("admin", null, "short"));
    }

    [Fact]
    public void The_two_passwords_have_to_differ()
    {
        var a = Bamf();
        a.SetPassword("admin", null, "same-password");
        Assert.NotNull(a.SetPassword("viewer", "same-password", "same-password"));
        a.SetPassword("viewer", "same-password", "viewer-password");
        Assert.NotNull(a.SetPassword("admin", "same-password", "viewer-password"));
    }

    [Fact]
    public void A_view_only_password_needs_a_main_one_first()
    {
        Assert.NotNull(Bamf().SetPassword("viewer", null, "viewer-password"));
    }

    [Fact]
    public void Removing_the_main_password_opens_BAMF_and_takes_the_view_only_one_too()
    {
        var a = Bamf();
        a.SetPassword("admin", null, "main-password");
        a.SetPassword("viewer", "main-password", "viewer-password");
        Assert.Null(a.SetPassword("admin", "main-password", ""));
        Assert.False(a.Required);
        a.SetPassword("admin", null, "main-password");
        Assert.Null(a.Source("viewer"));
    }

    [Fact]
    public void A_password_from_appsettings_cant_be_removed_here()
    {
        Assert.Contains("appsettings.json", Bamf("file-password").SetPassword("admin", "file-password", ""));
    }

    [Fact]
    public void Passwords_are_kept_hashed()
    {
        var hash = AuthService.Hash("main-password");
        Assert.DoesNotContain("main-password", hash);
        Assert.True(AuthService.Verify(hash, "main-password"));
        Assert.False(AuthService.Verify(hash, "main-passwore"));
        Assert.NotEqual(hash, AuthService.Hash("main-password"));   // salted
        Assert.False(AuthService.Verify("not a hash", "main-password"));
    }

    [Fact]
    public void Settings_survive_a_restart()
    {
        Bamf().SetPassword("admin", null, "main-password");
        Assert.Equal("admin", Bamf().RoleFor("main-password"));
    }

    [Fact]
    public void A_session_opens_what_its_password_opens()
    {
        var a = Bamf("file-password", "file-viewer");
        Assert.Equal("admin", a.Validate(a.Issue("admin"))?.Role);
        Assert.Equal("viewer", a.Validate(a.Issue("viewer"))?.Role);
    }

    [Fact]
    public void A_session_ends_after_30_days()
    {
        var a = Bamf("file-password");
        var s = a.Issue("admin");
        _now += TimeSpan.FromDays(29);
        Assert.NotNull(a.Validate(s));
        _now += TimeSpan.FromDays(2);
        Assert.Null(a.Validate(s));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("YWRtaW58OTk5OTk5OTk5OQ.AAAA")]
    public void A_made_up_session_opens_nothing(string token)
    {
        Assert.Null(Bamf("file-password").Validate(token));
    }

    [Fact]
    public void A_viewer_session_cant_be_turned_into_an_admin_one()
    {
        var a = Bamf("file-password", "file-viewer");
        var v = a.Issue("viewer");
        var forged = Convert.ToBase64String("admin"u8.ToArray()).TrimEnd('=') + v[v.IndexOf('.')..];
        Assert.Null(a.Validate(forged));
    }

    [Fact]
    public void Another_BAMFs_session_opens_nothing_here()
    {
        var other = Bamf("file-password", db: "other.db");
        Assert.Null(Bamf("file-password").Validate(other.Issue("admin")));
    }

    [Fact]
    public void Changing_the_main_password_ends_its_sessions_but_not_view_only_ones()
    {
        var a = Bamf();
        a.SetPassword("admin", null, "main-password");
        a.SetPassword("viewer", "main-password", "viewer-password");
        var admin = a.Issue("admin");
        var viewer = a.Issue("viewer");
        a.SetPassword("admin", "main-password", "new-main-password");
        Assert.Null(a.Validate(admin));
        Assert.NotNull(a.Validate(viewer));
    }

    [Fact]
    public void Removing_the_password_ends_every_session()
    {
        var a = Bamf();
        a.SetPassword("admin", null, "main-password");
        var s = a.Issue("admin");
        a.SetPassword("admin", "main-password", "");
        Assert.Null(a.Validate(s));
    }

    [Fact]
    public void Five_wrong_passwords_lock_an_address_out()
    {
        var a = Bamf("file-password");
        var reported = new List<(string, int)>();
        a.LockedOut = (addr, n) => reported.Add((addr, n));
        for (var i = 0; i < AuthService.MaxFailures - 1; i++) a.Failed("10.0.0.5");
        Assert.False(a.IsLocked("10.0.0.5", out _));
        a.Failed("10.0.0.5");
        Assert.True(a.IsLocked("10.0.0.5", out var left));
        Assert.Equal(AuthService.LockoutTime, left);
        Assert.False(a.IsLocked("10.0.0.6", out _));
        Assert.Equal([("10.0.0.5", AuthService.MaxFailures)], reported);
    }

    [Fact]
    public void A_lockout_wears_off()
    {
        var a = Bamf("file-password");
        for (var i = 0; i < AuthService.MaxFailures; i++) a.Failed("10.0.0.5");
        _now += AuthService.LockoutTime;
        Assert.False(a.IsLocked("10.0.0.5", out _));
    }

    [Fact]
    public void The_right_password_starts_the_count_again()
    {
        var a = Bamf("file-password");
        for (var i = 0; i < AuthService.MaxFailures - 1; i++) a.Failed("10.0.0.5");
        a.Succeeded("10.0.0.5");
        a.Failed("10.0.0.5");
        Assert.False(a.IsLocked("10.0.0.5", out _));
    }

    [Fact]
    public void Old_wrong_passwords_dont_count()
    {
        var a = Bamf("file-password");
        for (var i = 0; i < AuthService.MaxFailures - 1; i++) a.Failed("10.0.0.5");
        _now += AuthService.FailureWindow + TimeSpan.FromMinutes(1);
        a.Failed("10.0.0.5");
        Assert.False(a.IsLocked("10.0.0.5", out _));
    }
}
