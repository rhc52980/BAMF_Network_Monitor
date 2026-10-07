using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BAMF.Tests;

/// <summary>
/// What devices do: where they talk on the internet, scans, bandwidth spikes and a new device's first week,
/// on made-up packets and histories, and the switches and card in BAMF itself.
/// </summary>
public class FlowWatchTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 7, 14, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bamf-flow-" + Guid.NewGuid().ToString("N"));

    public FlowWatchTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private HostStore Store()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bamf:DatabasePath"] = Path.Combine(_dir, "bamf.db") }).Build();
        return new HostStore(config);
    }

    private static FlowWatch Watch(HostStore store) =>
        new(store, new TrafficMonitor(NullLogger<TrafficMonitor>.Instance, store), null, NullLogger<FlowWatch>.Instance, (_, _) => Task.FromResult<string?>(null));

    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    // ---------- what an address is ----------

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.4.4", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("169.254.1.1", true)]
    [InlineData("100.64.0.1", true)]     // carrier NAT
    [InlineData("224.0.0.251", true)]    // multicast
    [InlineData("255.255.255.255", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("185.220.101.4", false)]
    public void Local_addresses_are_told_from_the_internet(string ip, bool local) => Assert.Equal(local, FlowWatch.IsLocal(Ip(ip)));

    [Fact]
    public void A_destination_is_counted_by_its_slash_24() => Assert.Equal("203.0.113.0/24", FlowWatch.NetOf(Ip("203.0.113.77")));

    // ---------- where devices talk ----------

    [Fact]
    public void Outside_destinations_are_counted_and_kept_across_a_flush()
    {
        var store = Store();
        var w = Watch(store);
        w.Observe("AA:BB:CC:00:00:01", Ip("192.168.1.20"), Ip("203.0.113.5"), 6, 443, 1200, Now);
        w.Observe("AA:BB:CC:00:00:01", Ip("192.168.1.20"), Ip("203.0.113.9"), 6, 443, 300, Now);     // same /24
        w.Observe("AA:BB:CC:00:00:01", Ip("192.168.1.20"), Ip("198.51.100.1"), 17, 123, 76, Now);
        w.Observe("AA:BB:CC:00:00:01", Ip("192.168.1.20"), Ip("192.168.1.1"), 17, 53, 60, Now);       // local: not a destination
        w.Observe("AA:BB:CC:00:00:02", Ip("8.8.8.8"), Ip("192.168.1.20"), 17, 53, 60, Now);           // a reply from outside: not counted as that MAC's destination
        w.Flush();

        var rows = store.GetFlows("aa:bb:cc:00:00:01");
        Assert.Equal(2, rows.Count);
        var big = rows.Single(r => r.Net == "203.0.113.0/24");
        Assert.Equal(1500, big.Bytes);
        Assert.Equal(2, big.Hits);
        Assert.Equal("203.0.113.5", big.SampleIp);
        Assert.Empty(store.GetFlows("AA:BB:CC:00:00:02"));

        // A second flush adds to the row rather than replacing it.
        w.Observe("AA:BB:CC:00:00:01", Ip("192.168.1.20"), Ip("203.0.113.5"), 6, 443, 500, Now.AddMinutes(5));
        w.Flush();
        Assert.Equal(2000, store.GetFlows("AA:BB:CC:00:00:01").Single(r => r.Net == "203.0.113.0/24").Bytes);

        var s = w.Summary(Now).Single();
        Assert.Equal(2, s.Networks);
        Assert.Equal(2, s.NewThisWeek);
        Assert.Equal(2076, s.Bytes);
    }

    [Fact]
    public void A_fresh_watch_reads_what_an_earlier_one_kept()
    {
        var store = Store();
        store.AddFlows(new[] { new HostStore.FlowDelta("AA:BB:CC:00:00:01", "203.0.113.0/24", Now.AddDays(-10), 100, 1, "203.0.113.5") });
        var w = Watch(store);
        // The first packet to a new network from a device with history is a new destination; to the known one it isn't.
        w.Observe("AA:BB:CC:00:00:01", Ip("192.168.1.20"), Ip("203.0.113.6"), 6, 443, 100, Now);
        Assert.Equal(0, w.Pending.NewDestinations);
        w.Observe("AA:BB:CC:00:00:01", Ip("192.168.1.20"), Ip("198.51.100.1"), 6, 443, 100, Now);
        Assert.Equal(1, w.Pending.NewDestinations);
    }

    [Fact]
    public void Nothing_is_counted_while_the_watch_is_off()
    {
        var store = Store();
        store.SetSetting("flowWatch", "false");
        var w = Watch(store);
        w.Observe("AA:BB:CC:00:00:01", Ip("192.168.1.20"), Ip("203.0.113.5"), 6, 443, 1200, Now);
        w.Flush();
        Assert.Empty(store.GetFlows());
    }

    // ---------- scans ----------

    [Fact]
    public void Forty_local_addresses_inside_a_minute_is_a_scan()
    {
        var store = Store();
        var w = Watch(store);
        for (var i = 1; i <= 39; i++) w.Observe("AA:BB:CC:00:00:01", Ip("192.168.1.20"), Ip($"192.168.1.{100 + i}"), 6, 445, 60, Now.AddSeconds(i));
        Assert.Equal(0, w.Pending.Scans);
        w.Observe("AA:BB:CC:00:00:01", Ip("192.168.1.20"), Ip("192.168.1.140"), 6, 445, 60, Now.AddSeconds(40));
        Assert.Equal(1, w.Pending.Scans);
        // More of the same inside the hour is the same scan.
        for (var i = 1; i <= 60; i++) w.Observe("AA:BB:CC:00:00:01", Ip("192.168.1.20"), Ip($"192.168.1.{100 + i}"), 6, 445, 60, Now.AddMinutes(5).AddSeconds(i));
        Assert.Equal(1, w.Pending.Scans);
    }

    [Fact]
    public void Forty_ports_on_one_address_inside_a_minute_is_a_scan()
    {
        var store = Store();
        var w = Watch(store);
        for (var p = 1; p <= 40; p++) w.Observe("AA:BB:CC:00:00:01", Ip("192.168.1.20"), Ip("192.168.1.1"), 6, p, 60, Now.AddSeconds(p));
        Assert.Equal(1, w.Pending.Scans);
    }

    [Fact]
    public void Forty_addresses_spread_over_ten_minutes_is_not_a_scan()
    {
        var store = Store();
        var w = Watch(store);
        for (var i = 1; i <= 60; i++) w.Observe("AA:BB:CC:00:00:01", Ip("192.168.1.20"), Ip($"192.168.1.{100 + i}"), 6, 445, 60, Now.AddSeconds(i * 10));
        Assert.Equal(0, w.Pending.Scans);
    }

    [Fact]
    public void Talking_to_the_internet_is_not_a_scan()
    {
        var store = Store();
        var w = Watch(store);
        for (var i = 1; i <= 80; i++) w.Observe("AA:BB:CC:00:00:01", Ip("192.168.1.20"), Ip($"203.0.{i}.1"), 6, 443, 60, Now.AddSeconds(i));
        Assert.Equal(0, w.Pending.Scans);
    }

    // ---------- bandwidth spikes ----------

    [Fact]
    public void Five_times_the_usual_day_and_half_a_gigabyte_is_a_spike()
    {
        const long mb = 1024 * 1024;
        Assert.True(FlowWatch.IsSpike(3000 * mb, new long[] { 100 * mb, 200 * mb, 150 * mb }));
        Assert.False(FlowWatch.IsSpike(3000 * mb, new long[] { 100 * mb, 200 * mb }));            // only two days to compare with
        Assert.False(FlowWatch.IsSpike(400 * mb, new long[] { 10 * mb, 20 * mb, 15 * mb }));        // many times usual, but under half a gigabyte
        Assert.False(FlowWatch.IsSpike(3000 * mb, new long[] { 1000 * mb, 800 * mb, 900 * mb }));   // a big day, but only three times usual
        Assert.True(FlowWatch.IsSpike(600 * mb, new long[] { 0, 0, 0, 100 * mb }));                 // quiet days count as zero
    }

    [Fact]
    public void Daily_traffic_comes_from_the_hourly_history()
    {
        var store = Store();
        store.AddTraffic(new[]
        {
            ("AA:BB:CC:00:00:01", "2026-10-05T10:00:00Z", 100L, 50L),
            ("AA:BB:CC:00:00:01", "2026-10-05T11:00:00Z", 100L, 50L),
            ("AA:BB:CC:00:00:01", "2026-10-06T09:00:00Z", 10L, 10L),
            ("AA:BB:CC:00:00:02", "2026-10-06T09:00:00Z", 1L, 1L),
        });
        var days = store.DailyTraffic(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(300, days["AA:BB:CC:00:00:01"]["2026-10-05"]);
        Assert.Equal(20, days["AA:BB:CC:00:00:01"]["2026-10-06"]);
        Assert.Equal(2, days["AA:BB:CC:00:00:02"]["2026-10-06"]);
    }

    // ---------- a new device's first week ----------

    [Fact]
    public void Online_share_follows_the_events()
    {
        var from = Now.AddDays(-7);
        // Online from the start, off for one whole day in the middle.
        var events = new List<(DateTime, bool)> { (from.AddDays(3), false), (from.AddDays(4), true) };
        Assert.Equal(6.0 / 7, FlowWatch.OnlineShare(events, from, Now), 3);
        Assert.Equal(1.0, FlowWatch.OnlineShare(new List<(DateTime, bool)>(), from, Now));
        // Dropped two days ago and never came back.
        Assert.Equal(5.0 / 7, FlowWatch.OnlineShare(new List<(DateTime, bool)> { (from.AddDays(5), false) }, from, Now), 3);
    }

    [Fact]
    public void The_first_week_note_says_what_the_device_did()
    {
        var (title, detail) = FlowWatch.FirstWeekText("doorbell-cam", "Ring", "a camera", 0.99, new[] { 443, 80 }, 3, 1_300_000_000, stillUnknown: true);
        Assert.Equal("doorbell-cam: its first week on the network", title);
        Assert.Contains("doorbell-cam, a Ring device that looks like a camera, has been here a week", detail);
        Assert.Contains("online 99% of the time", detail);
        Assert.Contains("open ports 80, 443", detail);
        Assert.Contains("talked to 3 outside networks, moving 1.2 GB", detail);
        Assert.Contains("still marked unknown", detail);

        var (_, quiet) = FlowWatch.FirstWeekText("printer", "", "", 0.5, Array.Empty<int>(), 0, 0, stillUnknown: false);
        Assert.Contains("printer, has been here a week. It was online 50% of the time, has no open ports, and didn't talk to the internet that BAMF could see.", quiet);
        Assert.DoesNotContain("unknown", quiet);
    }

    // ---------- in BAMF ----------

    [Fact]
    public async Task The_card_and_the_switches_answer()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var d = await c.GetFromJsonAsync<JsonElement>("/api/flows");
        Assert.True(d.GetProperty("enabled").GetBoolean());
        Assert.True(d.GetProperty("spikeAlert").GetBoolean());
        Assert.True(d.GetProperty("firstWeekReport").GetBoolean());
        Assert.Equal(FlowWatch.LearnDays, d.GetProperty("learnDays").GetInt32());
        Assert.Empty(d.GetProperty("devices").EnumerateArray());

        foreach (var (url, key) in new[] { ("/api/settings/flow-watch", "flowWatch"), ("/api/settings/spike-alert", "spikeAlert"), ("/api/settings/first-week-report", "firstWeekReport") })
        {
            var r = await c.PostAsJsonAsync(url, new { enabled = false });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var e = await c.GetFromJsonAsync<JsonElement>("/api/settings");
            Assert.False(e.GetProperty("editable").GetProperty(key).GetBoolean());
        }
        d = await c.GetFromJsonAsync<JsonElement>("/api/flows");
        Assert.False(d.GetProperty("enabled").GetBoolean());
        Assert.False(d.GetProperty("spikeAlert").GetBoolean());
        Assert.False(d.GetProperty("firstWeekReport").GetBoolean());
    }
}
