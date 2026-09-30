using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BAMF.Tests;

/// <summary>
/// MQTT and the other BAMF servers, set in Settings → System. What's saved
/// there replaces appsettings.json's, and a saved password only ever goes back
/// to where it was saved for.
/// </summary>
public class IntegrationSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bamf-int-" + Guid.NewGuid().ToString("N"));

    public IntegrationSettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private (HostStore Store, IConfiguration Config) Setup(Dictionary<string, string?>? file = null)
    {
        var values = new Dictionary<string, string?> { ["Bamf:DatabasePath"] = Path.Combine(_dir, "bamf.db") };
        foreach (var (k, v) in file ?? []) values[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return (new HostStore(config), config);
    }

    private static MqttPublisher Mqtt(HostStore store, IConfiguration config) => new(store, config, NullLogger<MqttPublisher>.Instance);
    private static RemoteService Remotes(HostStore store, IConfiguration config) => new(config, null!, store, NullLogger<RemoteService>.Instance);

    private static void SaveMqtt(HostStore store, MqttPublisher.Saved saved) => store.SetSetting("mqtt", JsonSerializer.Serialize(saved));

    // ---------- MQTT ----------

    [Fact]
    public void Mqtt_is_off_with_nothing_set()
    {
        var (store, config) = Setup();
        var mqtt = Mqtt(store, config);
        Assert.Null(mqtt.Current());
        Assert.Null(mqtt.Source);
    }

    [Fact]
    public void Mqtt_from_the_file_is_used_until_settings_has_its_own()
    {
        var (store, config) = Setup(new() { ["Bamf:Mqtt:Server"] = "broker.lan", ["Bamf:Mqtt:Port"] = "1884", ["Bamf:Mqtt:Password"] = "file-pass" });
        var mqtt = Mqtt(store, config);
        Assert.Equal("file", mqtt.Source);
        Assert.Equal(("broker.lan", 1884, "file-pass"), (mqtt.Current()!.Server, mqtt.Current()!.Port, mqtt.Current()!.Password));

        SaveMqtt(store, new("10.0.0.9", 1883, "u", "settings-pass", false, false, "home/bamf"));
        Assert.Equal("settings", mqtt.Source);
        var c = mqtt.Current()!;
        Assert.Equal(("10.0.0.9", 1883, "settings-pass", false, "home/bamf"), (c.Server, c.Port, c.Password, c.Discovery, c.TopicPrefix));
    }

    [Fact]
    public void A_blank_broker_in_settings_turns_mqtt_off_even_with_one_in_the_file()
    {
        var (store, config) = Setup(new() { ["Bamf:Mqtt:Server"] = "broker.lan" });
        var mqtt = Mqtt(store, config);
        SaveMqtt(store, new("", 1883, null, null, false, true, "bamf"));
        Assert.Null(mqtt.Current());
        Assert.Equal("settings", mqtt.Source);
    }

    [Fact]
    public void Removing_the_settings_hands_mqtt_back_to_the_file()
    {
        var (store, config) = Setup(new() { ["Bamf:Mqtt:Server"] = "broker.lan" });
        var mqtt = Mqtt(store, config);
        SaveMqtt(store, new("10.0.0.9", 1883, null, null, false, true, "bamf"));
        store.DeleteSetting("mqtt");
        Assert.Equal(("file", "broker.lan"), (mqtt.Source, mqtt.Current()!.Server));
    }

    [Fact]
    public void A_change_to_the_settings_is_a_different_connection()
    {
        // The publisher reconnects when Current() stops equalling what it connected with.
        var (store, config) = Setup();
        var mqtt = Mqtt(store, config);
        SaveMqtt(store, new("10.0.0.9", 1883, "u", "p", false, true, "bamf"));
        var before = mqtt.Current();
        Assert.Equal(before, mqtt.Current());
        SaveMqtt(store, new("10.0.0.9", 1883, "u", "p", false, false, "bamf"));
        Assert.NotEqual(before, mqtt.Current());
    }

    private static readonly MqttPublisher.MqttSettings Saved =
        new("broker.lan", 1883, "u", "saved-pass", false, true, "bamf", "homeassistant", "bamf");

    [Theory]
    [InlineData("broker.lan", 1883, "saved-pass")]
    [InlineData("BROKER.lan", 1883, "saved-pass")]   // a name's case isn't a different broker
    [InlineData("other.lan", 1883, null)]            // another broker never gets it
    [InlineData("broker.lan", 1884, null)]           // nor another port on it
    public void The_saved_mqtt_password_goes_to_the_same_broker_only(string server, int port, string? kept)
    {
        Assert.Equal(kept, MqttPublisher.KeptPassword(Saved, server, port, typed: null));
    }

    [Fact]
    public void A_typed_mqtt_password_is_always_used()
    {
        Assert.Equal("new-pass", MqttPublisher.KeptPassword(Saved, "other.lan", 1883, "new-pass"));
        Assert.Null(MqttPublisher.KeptPassword(null, "broker.lan", 1883, null));
    }

    // ---------- Other BAMF servers ----------

    [Fact]
    public void Servers_from_the_file_are_used_until_settings_has_a_list()
    {
        var (store, config) = Setup(new() { ["Bamf:Remotes:0:Name"] = "Cabin", ["Bamf:Remotes:0:Url"] = "http://10.1.0.5:8840" });
        var remotes = Remotes(store, config);
        Assert.Equal("file", remotes.Source);
        Assert.Equal("Cabin", Assert.Single(remotes.Remotes).Name);

        store.SetSetting("remotes", JsonSerializer.Serialize(new List<RemoteService.Remote> { new("Office", "http://10.2.0.5:8840", null) }));
        Assert.Equal("settings", remotes.Source);
        Assert.Equal("Office", Assert.Single(remotes.Remotes).Name);
    }

    [Fact]
    public void An_empty_list_in_settings_means_none_even_with_some_in_the_file()
    {
        var (store, config) = Setup(new() { ["Bamf:Remotes:0:Name"] = "Cabin", ["Bamf:Remotes:0:Url"] = "http://10.1.0.5:8840" });
        var remotes = Remotes(store, config);
        store.SetSetting("remotes", "[]");
        Assert.Empty(remotes.Remotes);
        Assert.False(remotes.Configured);
        Assert.Equal("settings", remotes.Source);
    }

    private static readonly List<RemoteService.Remote> SavedRemotes = [new("Cabin", "http://10.1.0.5:8840", "saved-pass")];

    [Theory]
    [InlineData("Cabin", "http://10.1.0.5:8840", "saved-pass")]
    [InlineData("cabin", "http://10.1.0.5:8840/", "saved-pass")]   // same server, written a little differently
    [InlineData("Cabin", "http://10.9.9.9:8840", null)]            // moved: the password isn't sent there
    [InlineData("Cabin", "https://10.1.0.5:8840", null)]
    [InlineData("Lake", "http://10.1.0.5:8840", null)]             // a different entry
    public void A_servers_saved_password_stays_only_while_its_name_and_address_do(string name, string url, string? kept)
    {
        Assert.Equal(kept, RemoteService.KeptPassword(SavedRemotes, name, url, typed: null));
    }

    [Fact]
    public void A_typed_server_password_is_always_used()
    {
        Assert.Equal("new-pass", RemoteService.KeptPassword(SavedRemotes, "Cabin", "http://10.9.9.9:8840", "new-pass"));
    }
}
