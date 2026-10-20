using LanWatch.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BAMF.Tests;

/// <summary>
/// The DHCP and DNS servers on the Network services card survive a restart: a second monitor on the same database lists what the first heard.
/// </summary>
public class KeepServersTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bamf-keep-" + Guid.NewGuid().ToString("N"));

    public KeepServersTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private HostStore Store() =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bamf:DatabasePath"] = Path.Combine(_dir, "bamf.db") }).Build());

    private static TrafficMonitor Monitor(HostStore store) => new(NullLogger<TrafficMonitor>.Instance, store);

    [Fact]
    public void A_dhcp_server_found_is_still_listed_after_a_restart()
    {
        var store = Store();
        var first = Monitor(store);
        first.NoteDhcpServer("192.168.2.1", "aa:bb:cc:00:00:01", "192.168.2.0/24");
        first.NoteDhcpServer("192.168.2.1", null, "192.168.2.0/24");

        var second = Monitor(store);
        var s = Assert.Single(second.DhcpServers());
        Assert.Equal("192.168.2.1", s.Ip);
        Assert.Equal("aa:bb:cc:00:00:01", s.Mac);
        Assert.Equal(2, s.Offers);
        Assert.False(string.IsNullOrEmpty(s.LastSeen));
        Assert.Contains("192.168.2.1", second.KnownDhcp);
    }

    [Fact]
    public void A_dns_server_heard_is_still_listed_with_its_counts_after_a_restart()
    {
        var store = Store();
        var first = Monitor(store);
        first.Dns("aa:bb:cc:00:00:01", "192.168.2.1");
        first.Dns("aa:bb:cc:00:00:01", "192.168.2.1");
        first.Dns("aa:bb:cc:00:00:02", "192.168.2.1");
        first.FlushHourly();                                                     // the five-minute write, which carries the lists

        var second = Monitor(store);
        var s = Assert.Single(second.DnsServers());
        Assert.Equal("192.168.2.1", s.Ip);
        Assert.Equal(2, s.Clients);
        Assert.Equal(3, s.Queries);
        Assert.Equal(new[] { "192.168.2.1" }, second.DnsByDevice()["aa:bb:cc:00:00:01"]);
    }

    [Fact]
    public void Disposing_a_monitor_that_is_not_capturing_still_writes_what_it_heard()
    {
        var store = Store();
        var first = Monitor(store);
        first.Dns("aa:bb:cc:00:00:01", "192.168.2.1");
        first.Dispose();
        Assert.Single(Monitor(store).DnsServers());
    }

    [Fact]
    public void A_trusted_server_not_heard_since_is_still_listed_and_forgetting_it_removes_it()
    {
        var store = Store();
        var first = Monitor(store);
        first.Trust("dhcp", "192.168.2.9", true);                                // trusted before anything was kept of it being heard

        var second = Monitor(store);
        var s = Assert.Single(second.DhcpServers());
        Assert.Equal("192.168.2.9", s.Ip);
        Assert.Equal("", s.LastSeen);
        Assert.Equal(0, s.Offers);

        second.Trust("dhcp", "192.168.2.9", false);
        Assert.Empty(second.DhcpServers());
    }

    [Fact]
    public void A_server_not_heard_for_ninety_days_is_dropped_but_a_recent_one_stays()
    {
        var store = Store();
        var old = DateTime.UtcNow.AddDays(-120).ToString("o");
        var recent = DateTime.UtcNow.AddDays(-5).ToString("o");
        store.SetSetting("dhcpServersSeen", $"{{\"10.0.0.1\":{{\"Mac\":\"\",\"Last\":\"{old}\",\"Offers\":4}},\"10.0.0.2\":{{\"Mac\":\"\",\"Last\":\"{recent}\",\"Offers\":9}}}}");

        var s = Assert.Single(Monitor(store).DhcpServers());
        Assert.Equal("10.0.0.2", s.Ip);
        Assert.Equal(9, s.Offers);
    }

    [Fact]
    public void A_damaged_saved_list_only_means_an_empty_card()
    {
        var store = Store();
        store.SetSetting("dhcpServersSeen", "{not json");
        store.SetSetting("dnsServersSeen", "[1,2");
        var m = Monitor(store);
        Assert.Empty(m.DhcpServers());
        Assert.Empty(m.DnsServers());
    }
}
