using System.Text.Json;
using System.Text.Json.Nodes;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>Settings export and import: what goes in the file, what is kept out, what doesn't carry between servers, and that a bad file changes nothing.</summary>
[Collection("Endpoints")]
public class SettingsTransferTests
{
    private static JsonElement Doc(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public void Nothing_secret_and_nothing_about_this_site_is_in_the_file()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        store.SetSetting("webhookUrl", "https://discord.com/api/webhooks/1/SECRET");
        store.SetSetting("alertDestinations", "[{\"url\":\"https://ntfy.sh/SECRETTOPIC\"}]");
        store.SetSetting("mqtt", "{\"password\":\"SECRETPASS\"}");
        store.SetSetting("hookToken", "SECRETTOKEN");
        store.SetSetting("backupCopyTo", "D:\\secret");
        store.SetSetting("subnets", "192.168.2.0/24");
        store.SetSetting("gatewayMacs", "aa:bb");
        store.SetSetting("quietFrom", "23:00");
        store.SetSetting("remotes", JsonSerializer.Serialize(new List<RemoteService.Remote> { new("Cabin", "http://10.0.0.5:8840", "SECRETREMOTE") }));
        var text = store.ExportSettings("2.3.0").ToJsonString();
        foreach (var secret in new[] { "SECRET", "192.168.2.0", "gatewayMacs", "D:" }) Assert.DoesNotContain(secret, text);
        var root = JsonNode.Parse(text)!;
        Assert.Equal("23:00", (string?)root["settings"]!["quietFrom"]);
        Assert.Equal("Cabin", (string?)root["remotes"]![0]!["name"]);
        Assert.Equal("http://10.0.0.5:8840", (string?)root["remotes"]![0]!["url"]);
    }

    [Fact]
    public void Alert_rules_about_one_device_stay_behind_because_the_number_means_nothing_elsewhere()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        store.SetSetting("alertRules", JsonSerializer.Serialize(new List<RuleService.Rule>
        {
            new("a", "Kids at night", "hours", "tag:kids", 0, "22:00", "06:00", true),
            new("b", "The NAS", "offline", "host:12", 15, "", "", true),
            new("c", "Anything", "offline", "any", 30, "", "", true),
        }));
        var root = store.ExportSettings("2.3.0");
        Assert.Equal(["a", "c"], root["alertRules"]!.AsArray().Select(r => (string?)r!["id"]).ToArray());
    }

    [Fact]
    public async Task A_file_from_one_server_sets_another_the_same_way_and_leaves_its_own_secrets_alone()
    {
        string file;
        using (var a = new BamfApp())
        {
            var s = a.Services.GetRequiredService<HostStore>();
            s.SetSetting("quietFrom", "22:30"); s.SetSetting("quietTo", "06:30"); s.SetSetting("wanWatch", "true");
            s.SetSetting("alertRules", JsonSerializer.Serialize(new List<RuleService.Rule> { new("a", "Kids", "hours", "tag:kids", 0, "22:00", "06:00", true) }));
            file = await a.Client().GetStringAsync("/api/settings/export");
        }
        using var b = new BamfApp();
        var store = b.Services.GetRequiredService<HostStore>();
        store.SetSetting("webhookUrl", "https://discord.com/api/webhooks/9/KEEPME");
        store.SetSetting("subnets", "10.9.0.0/24");
        var r = await b.Client().PostAsync("/api/settings/import", Json(file));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Equal("22:30", store.GetSetting("quietFrom"));
        Assert.Equal("true", store.GetSetting("wanWatch"));
        Assert.Contains("Kids", store.GetSetting("alertRules"));
        Assert.Equal("https://discord.com/api/webhooks/9/KEEPME", store.GetSetting("webhookUrl"));
        Assert.Equal("10.9.0.0/24", store.GetSetting("subnets"));
    }

    [Fact]
    public async Task A_server_already_listed_keeps_its_saved_password_through_an_import()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        store.SetSetting("remotes", JsonSerializer.Serialize(new List<RemoteService.Remote> { new("Cabin", "http://10.0.0.5:8840", "KEEPPASS") }));
        var doc = """{"bamfSettings":1,"remotes":[{"name":"Cabin","url":"http://10.0.0.5:8840"},{"name":"Office","url":"http://10.0.0.6:8840"}]}""";
        var r = await app.Client().PostAsync("/api/settings/import", Json(doc));
        Assert.True(r.IsSuccessStatusCode);
        var saved = JsonSerializer.Deserialize<List<RemoteService.Remote>>(store.GetSetting("remotes")!)!;
        Assert.Equal("KEEPPASS", saved.Single(x => x.Name == "Cabin").Password);
        Assert.Null(saved.Single(x => x.Name == "Office").Password);
    }

    [Fact]
    public void Rules_about_one_device_in_a_file_are_skipped_and_said_so()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var doc = Doc("""{"bamfSettings":1,"alertRules":[{"id":"a","name":"x","kind":"offline","target":"host:3","minutes":5,"from":"","to":"","enabled":true},{"id":"b","name":"y","kind":"offline","target":"any","minutes":5,"from":"","to":"","enabled":true}]}""");
        var (result, error) = store.ImportSettings(doc);
        Assert.Null(error);
        Assert.Equal(1, result!.RulesApplied);
        Assert.Equal(1, result.RulesSkipped);
    }

    [Theory]
    [InlineData("""{"hello":1}""", "isn't a BAMF settings file")]
    [InlineData("""{"bamfSettings":1,"settings":{"quietFrom":5}}""", "should be text")]
    [InlineData("""{"bamfSettings":1,"remotes":[{"name":"Cabin","url":"ftp://x"}]}""", "no usable name or address")]
    [InlineData("""{"bamfSettings":1,"alertRules":[{"id":"","name":"x","kind":"offline","target":"any","minutes":5,"from":"","to":"","enabled":true}]}""", "aren't ones BAMF could use")]
    public void A_bad_file_is_refused_and_changes_nothing(string json, string why)
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        store.SetSetting("quietFrom", "21:00");
        var (result, error) = store.ImportSettings(Doc(json));
        Assert.Null(result);
        Assert.Contains(why, error);
        Assert.Equal("21:00", store.GetSetting("quietFrom"));
    }

    [Fact]
    public void Keys_it_doesnt_carry_are_ignored_not_applied()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var (result, error) = store.ImportSettings(Doc("""{"bamfSettings":1,"settings":{"webhookUrl":"https://evil.example/x","hookToken":"t","quietFrom":"20:00"}}"""));
        Assert.Null(error);
        Assert.Equal(1, result!.Applied);
        Assert.Equal(["webhookUrl", "hookToken"], result.Ignored);
        Assert.Null(store.GetSetting("webhookUrl"));
        Assert.Equal("20:00", store.GetSetting("quietFrom"));
    }
}
