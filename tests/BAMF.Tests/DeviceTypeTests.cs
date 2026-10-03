using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>The device types a Map icon can be picked from: each is accepted, remembered and shown, and a made-up one isn't.</summary>
[Collection("Endpoints")]
public class DeviceTypeTests
{
    [Fact]
    public void Every_type_is_listed_once_and_is_lower_case()
    {
        var keys = HostStore.DeviceTypeKeys;
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.All(keys, k => Assert.Matches("^[a-z0-9]+$", k));
        Assert.Contains("streamer", keys);
        Assert.Contains("thermostat", keys);
        Assert.Equal("device", keys[^1]);
    }

    [Fact]
    public async Task A_device_can_be_given_any_of_the_types_and_it_comes_back_in_the_list()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        store.UpsertSeen("aa:bb:cc:00:10:01", "192.168.30.60", "device-60", "Acme", "192.168.30.0/24");
        var id = store.GetAll().Single(h => h.Mac == "aa:bb:cc:00:10:01").Id;
        var c = app.Client();
        foreach (var type in HostStore.DeviceTypeKeys)
        {
            var r = await c.PostAsJsonAsync($"/api/hosts/{id}/type", new { type });
            Assert.True(r.IsSuccessStatusCode, $"{type}: {await r.Content.ReadAsStringAsync()}");
            var hosts = await c.GetFromJsonAsync<JsonElement>("/api/hosts");
            var mine = hosts.GetProperty("hosts").EnumerateArray().Single(h => h.GetProperty("id").GetInt64() == id);
            Assert.Equal(type, mine.GetProperty("deviceType").GetString());
        }
    }

    [Fact]
    public async Task A_type_that_isnt_one_is_refused_and_leaves_the_device_alone()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        store.UpsertSeen("aa:bb:cc:00:10:02", "192.168.30.61", "device-61", "Acme", "192.168.30.0/24");
        var id = store.GetAll().Single(h => h.Mac == "aa:bb:cc:00:10:02").Id;
        Assert.Equal(null, store.SetDeviceType(id, "doorbell"));
        Assert.NotNull(store.SetDeviceType(id, "toaster"));
        Assert.Equal("doorbell", store.GetDeviceTypes()[id]);
    }
}
