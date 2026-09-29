using LanWatch.Services;

namespace BAMF.Tests;

/// <summary>The Home Assistant add-on's options, as BAMF's settings.</summary>
public class HomeAssistantAddonTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "bamf-options-" + Guid.NewGuid().ToString("N") + ".json");
    public void Dispose() { try { File.Delete(_file); } catch (IOException) { } }

    private Dictionary<string, string?> Read(string json)
    {
        File.WriteAllText(_file, json);
        return HomeAssistantAddon.Read(_file);
    }

    [Fact]
    public void The_passwords_come_through()
    {
        var map = Read("""{ "password": "main-password", "viewer_password": "viewer-password" }""");
        Assert.Equal("main-password", map["Bamf:Password"]);
        Assert.Equal("viewer-password", map["Bamf:ViewerPassword"]);
    }

    [Theory]
    [InlineData("true", "True")]
    [InlineData("false", "False")]
    public void Reset_a_forgotten_password_comes_through(string option, string setting)
    {
        Assert.Equal(setting, Read($$"""{ "reset_password": {{option}} }""")["Bamf:ResetPassword"]);
    }

    [Fact]
    public void Without_the_option_nothing_is_reset()
    {
        Assert.False(Read("{}").ContainsKey("Bamf:ResetPassword"));
    }
}
