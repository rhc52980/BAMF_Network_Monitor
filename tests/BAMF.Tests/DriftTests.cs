using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>
/// The quieter changes to a device: its name changing, its moving to another network, and a different device taking over an address one
/// still online was answering at. Each goes in the device's story and in What changed; the takeover alerts, with a switch.
/// </summary>
[Collection("Endpoints")]
public class DriftTests
{
    private static long IdOf(HostStore store, string mac) => store.GetAll().Single(h => h.Mac == mac).Id;

    [Fact]
    public async Task A_name_that_changes_is_written_down_but_a_lookup_that_failed_or_a_junk_name_is_not()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        store.UpsertSeen("ee:00:00:00:50:01", "192.168.30.201", "study-pc", "Dell", "192.168.30.0/24");
        store.UpsertSeen("ee:00:00:00:50:01", "192.168.30.201", "", "Dell", "192.168.30.0/24");                     // reverse DNS failed this time: no change
        store.UpsertSeen("ee:00:00:00:50:01", "192.168.30.201", "Study-PC", "Dell", "192.168.30.0/24");             // only the case
        store.UpsertSeen("ee:00:00:00:50:01", "192.168.30.201", "garage-pc", "Dell", "192.168.30.0/24");            // a real change
        var id = IdOf(store, "ee:00:00:00:50:01");
        var d = Assert.Single(store.GetDrift(id));
        Assert.Equal("name", d.Kind);
        Assert.Equal("Study-PC", d.Before);                                                                   // the case-only change was kept as the name, not written down
        Assert.Equal("garage-pc", d.After);
        Assert.Contains(store.Timeline(id), t => t.Kind == "change" && t.Text == "Name changed to garage-pc (was Study-PC)");

        var tl = await app.Client().GetFromJsonAsync<JsonElement>($"/api/hosts/{id}/timeline");
        Assert.Contains(tl.EnumerateArray(), t => t.GetProperty("kind").GetString() == "change");
    }

    [Fact]
    public void Moving_to_another_network_is_written_down()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        store.UpsertSeen("ee:00:00:00:50:02", "192.168.30.202", "laptop", "Apple", "192.168.30.0/24");
        // Its old address stops answering (a second address while the first still answers is "also at", not a move), then it turns up on the guest network.
        store.SweepAddresses(DateTime.UtcNow.AddMinutes(1), null, 1, new[] { "192.168.30.0/24", "192.168.209.0/24" });
        store.UpsertSeen("ee:00:00:00:50:02", "192.168.209.40", "laptop", "Apple", "192.168.209.0/24");
        var drift = store.GetDrift(IdOf(store, "ee:00:00:00:50:02"));
        var d = Assert.Single(drift, x => x.Kind == "network");
        Assert.Equal("192.168.30.0/24", d.Before);
        Assert.Equal("192.168.209.0/24", d.After);
        Assert.Equal("Moved to 192.168.209.0/24 (was on 192.168.30.0/24)", HostStore.DriftText(d));
    }

    [Fact]
    public async Task A_new_device_at_an_address_another_still_online_was_answering_at_is_a_takeover_which_alerts_once()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var scanner = app.Services.GetRequiredService<ScannerService>();
        store.UpsertSeen("ee:00:00:00:50:03", "192.168.30.203", "printer", "Brother", "192.168.30.0/24");
        store.DrainDrift();
        store.UpsertSeen("ee:00:00:00:50:04", "192.168.30.203", "", "Espressif", "192.168.30.0/24");            // a different thing, same address, printer still online
        var taker = IdOf(store, "ee:00:00:00:50:04");
        var d = Assert.Single(store.GetDrift(taker));
        Assert.Equal("takeover", d.Kind);
        Assert.Equal("192.168.30.203", d.After);
        Assert.Contains("printer", d.Before);
        Assert.Contains("ee:00:00:00:50:03", d.Before);
        Assert.Empty(store.GetDrift(IdOf(store, "ee:00:00:00:50:03")));                                       // the victim's story isn't written on

        await scanner.RaiseDrift(CancellationToken.None);
        var alert = Assert.Single(store.GetAlerts(50).Where(a => a.Title.Contains("took over")));
        Assert.Contains("192.168.30.203", alert.Title);
        Assert.Contains("printer", alert.Detail);
        // raised once: the drain is empty now, and the same pair is held for a day
        store.UpsertSeen("ee:00:00:00:50:04", "192.168.30.203", "", "Espressif", "192.168.30.0/24");
        await scanner.RaiseDrift(CancellationToken.None);
        Assert.Single(store.GetAlerts(50).Where(a => a.Title.Contains("took over")));

        var changes = await app.Client().GetFromJsonAsync<JsonElement>("/api/changes?days=1");
        var drifted = changes.GetProperty("drifted").EnumerateArray().ToList();
        Assert.Contains(drifted, x => x.GetProperty("hostId").GetInt64() == taker && x.GetProperty("detail").GetString()!.StartsWith("Took over 192.168.30.203"));
    }

    [Fact]
    public void An_address_a_device_that_has_gone_offline_left_behind_or_the_same_kind_of_device_is_not_a_takeover()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        store.UpsertSeen("ee:00:00:00:50:05", "192.168.30.205", "old-phone", "(randomized MAC)", "192.168.30.0/24");
        store.UpsertSeen("ee:00:00:00:50:06", "192.168.30.205", "", "(randomized MAC)", "192.168.30.0/24");    // the same phone with a fresh private address
        Assert.Empty(store.GetDrift(IdOf(store, "ee:00:00:00:50:06")));

        store.UpsertSeen("ee:00:00:00:50:07", "192.168.30.207", "tablet", "Samsung", "192.168.30.0/24");
        store.MarkOffline(new HashSet<string>(), new[] { "192.168.30.0/24" }, 1);                                                // everything missed enough scans: offline
        Assert.False(store.GetAll().Single(h => h.Mac == "ee:00:00:00:50:07").Online);
        store.UpsertSeen("ee:00:00:00:50:08", "192.168.30.207", "", "Sonos", "192.168.30.0/24");             // DHCP handed the lease on
        Assert.Empty(store.GetDrift(IdOf(store, "ee:00:00:00:50:08")));
    }

    [Fact]
    public async Task The_takeover_alert_has_a_switch_which_the_settings_file_carries()
    {
        using var app = new BamfApp();
        var store = app.Services.GetRequiredService<HostStore>();
        var scanner = app.Services.GetRequiredService<ScannerService>();
        var c = app.Client();
        Assert.True((await c.PostAsJsonAsync("/api/settings/takeover-alert", new { enabled = false })).IsSuccessStatusCode);
        Assert.False(scanner.TakeoverAlertEnabled);
        var settings = await c.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.False(settings.GetProperty("editable").GetProperty("takeoverAlert").GetBoolean());
        Assert.Contains("takeoverAlert", HostStore.TransferKeys);

        store.UpsertSeen("ee:00:00:00:50:09", "192.168.30.209", "nas", "Synology", "192.168.30.0/24");
        store.UpsertSeen("ee:00:00:00:50:0a", "192.168.30.209", "", "Espressif", "192.168.30.0/24");
        Assert.Single(store.GetDrift(IdOf(store, "ee:00:00:00:50:0a")));                                     // still in the story
        await scanner.RaiseDrift(CancellationToken.None);
        Assert.DoesNotContain(store.GetAlerts(50), a => a.Title.Contains("took over"));                      // but no alert
    }
}
