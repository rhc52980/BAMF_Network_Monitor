using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>
/// "Same phone, new address?": records with a made-up MAC that share a name and are never online together are offered as one device. Generic
/// names, real MACs, two online at once and records already combined are left alone; dismissing, combining and the switch.
/// </summary>
[Collection("Endpoints")]
public class SameDeviceTests
{
    private const string Net = "192.168.30.0/24";
    // A locally administered MAC (second hex digit 2, 6, A or E) is what OuiLookup calls "(randomized MAC)".
    private static string Mac(int n) => $"ea:00:00:00:61:{n:x2}";

    private static void Seen(HostStore s, int n, string name, string ip = "") =>
        s.UpsertSeen(Mac(n), ip != "" ? ip : $"192.168.30.{100 + n}", name, "(randomized MAC)", Net);

    /// <summary>Everything but the given MACs goes offline.</summary>
    private static void OnlyThese(HostStore s, params int[] online) =>
        s.MarkOffline(online.Select(Mac).ToHashSet(StringComparer.OrdinalIgnoreCase), new[] { Net }, 1);

    [Fact]
    public void Records_with_made_up_addresses_and_one_name_are_offered_as_one_device_keeping_the_one_seen_last()
    {
        using var app = new BamfApp();
        var s = app.Services.GetRequiredService<HostStore>();
        Seen(s, 1, "Ruths-Pixel");
        OnlyThese(s);
        Seen(s, 2, "ruths-pixel.local");                                                                              // a newer address, the name in another dress
        var g = Assert.Single(s.SameDeviceGroups());
        Assert.Equal("ruths-pixel", g.Name);
        Assert.Equal(2, g.Members.Count);
        Assert.Equal(g.Members[0].Id, g.KeepId);
        Assert.Equal(Mac(2), g.Members[0].Mac);                                                                       // the one online now is the one the others join
    }

    [Fact]
    public void Generic_names_real_addresses_and_one_off_names_are_not_offered()
    {
        using var app = new BamfApp();
        var s = app.Services.GetRequiredService<HostStore>();
        Seen(s, 1, "iPhone"); OnlyThese(s); Seen(s, 2, "iPhone");                                                      // every iPhone is "iPhone"
        Seen(s, 3, "only-one");                                                                                        // nothing to join
        s.UpsertSeen("00:11:22:33:44:55", "192.168.30.50", "desk-pc", "Dell", Net);
        s.UpsertSeen("00:11:22:33:44:66", "192.168.30.51", "desk-pc", "Dell", Net);                                    // real MACs: a card on two networks is the other tool's
        Assert.Empty(s.SameDeviceGroups());
    }

    [Fact]
    public void Two_online_at_once_are_two_devices()
    {
        using var app = new BamfApp();
        var s = app.Services.GetRequiredService<HostStore>();
        Seen(s, 1, "kitchen-tablet");
        Seen(s, 2, "kitchen-tablet");                                                                                  // both answered in the same scan
        Assert.Empty(s.SameDeviceGroups());
        OnlyThese(s, 1);
        Assert.Single(s.SameDeviceGroups());
    }

    [Fact]
    public void A_group_too_big_to_be_one_phone_is_not_offered()
    {
        using var app = new BamfApp();
        var s = app.Services.GetRequiredService<HostStore>();
        for (var i = 1; i <= HostStore.MostInAGroup + 1; i++) { Seen(s, i, "guest-phone"); OnlyThese(s); }
        Assert.Empty(s.SameDeviceGroups());
    }

    [Fact]
    public async Task Combining_joins_the_older_records_into_the_newest_and_separate_undoes_it()
    {
        using var app = new BamfApp();
        var s = app.Services.GetRequiredService<HostStore>();
        var c = app.Client();
        Seen(s, 1, "ruths-pixel"); OnlyThese(s);
        Seen(s, 2, "ruths-pixel"); OnlyThese(s);
        Seen(s, 3, "ruths-pixel");
        var list = await c.GetFromJsonAsync<JsonElement>("/api/same-device");
        Assert.True(list.GetProperty("enabled").GetBoolean());
        var g = Assert.Single(list.GetProperty("groups").EnumerateArray());
        var key = g.GetProperty("key").GetString()!;
        Assert.Equal(3, g.GetProperty("members").GetArrayLength());

        var done = await c.PostAsJsonAsync("/api/same-device/combine", new { key });
        Assert.True(done.IsSuccessStatusCode);
        Assert.Equal(2, (await done.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("combined").GetInt32());

        var keep = s.GetAll().Single(h => h.Mac == Mac(3)).Id;
        var joined = s.GetInterfaces();
        Assert.Equal(2, joined.Count);
        Assert.All(joined.Values, v => Assert.Equal(keep, v));
        Assert.Empty(s.SameDeviceGroups());                                                                            // nothing left to offer

        var again = await c.PostAsJsonAsync("/api/same-device/combine", new { key });                                  // the same suggestion twice
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, again.StatusCode);

        Assert.Null(s.SetInterfaceOf(s.GetAll().Single(h => h.Mac == Mac(1)).Id, 0));                                  // Separate
        Assert.Single(s.GetInterfaces());
    }

    [Fact]
    public async Task Only_a_suggestion_that_is_on_offer_can_be_combined()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var r = await c.PostAsJsonAsync("/api/same-device/combine", new { key = "nobody|1,2" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/same-device/dismiss", new { key = "" })).StatusCode);
    }

    [Fact]
    public async Task A_dismissed_suggestion_stays_away_until_another_record_joins_it()
    {
        using var app = new BamfApp();
        var s = app.Services.GetRequiredService<HostStore>();
        var c = app.Client();
        Seen(s, 1, "den-laptop"); OnlyThese(s);
        Seen(s, 2, "den-laptop");
        var key = Assert.Single(s.SameDeviceGroups()).Key;
        Assert.True((await c.PostAsJsonAsync("/api/same-device/dismiss", new { key })).IsSuccessStatusCode);
        Assert.Empty(s.SameDeviceGroups());

        OnlyThese(s); Seen(s, 3, "den-laptop");
        var g = Assert.Single(s.SameDeviceGroups());                                                                    // three now: a different suggestion
        Assert.Equal(3, g.Members.Count);
        Assert.NotEqual(key, g.Key);
    }

    [Fact]
    public async Task The_switch_turns_the_suggestions_off_and_is_saved_with_the_settings()
    {
        using var app = new BamfApp();
        var s = app.Services.GetRequiredService<HostStore>();
        var c = app.Client();
        Seen(s, 1, "den-laptop"); OnlyThese(s); Seen(s, 2, "den-laptop");
        Assert.Single((await c.GetFromJsonAsync<JsonElement>("/api/same-device")).GetProperty("groups").EnumerateArray());

        Assert.True((await c.PostAsJsonAsync("/api/settings/same-device-hints", new { enabled = false })).IsSuccessStatusCode);
        var off = await c.GetFromJsonAsync<JsonElement>("/api/same-device");
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, off.GetProperty("groups").GetArrayLength());
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("editable").GetProperty("sameDeviceHints").GetBoolean());
        Assert.Contains("sameDeviceHints", HostStore.TransferKeys);
        Assert.Contains("dnsBypassAlert", HostStore.TransferKeys);
        Assert.Contains("dnsBypassWatch", HostStore.TransferKeys);
    }
}
