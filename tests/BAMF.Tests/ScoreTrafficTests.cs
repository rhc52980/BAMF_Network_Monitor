using LanWatch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>
/// The network score counts the traffic monitor's alerts (a new DHCP or DNS server, a device changing DNS server) among
/// the week's security alerts. They are kept apart from the other alerts, and weren't counted before.
/// </summary>
[Collection("Endpoints")]
public class ScoreTrafficTests
{
    [Fact]
    public void A_new_dhcp_or_dns_server_or_a_device_changing_dns_this_week_counts()
    {
        using var app = new BamfApp();
        var score = app.Services.GetRequiredService<HealthScore>();
        var traffic = app.Services.GetRequiredService<TrafficMonitor>();
        Assert.Equal(0, score.Gather().SecurityAlerts);

        traffic.NoteDhcpServer("192.168.30.1", "AA:BB:CC:00:00:01", "192.168.30.0/24");      // the first server is learned quietly
        traffic.NoteDhcpServer("192.168.30.66", "AA:BB:CC:00:00:66", "192.168.30.0/24");     // a second one alerts
        traffic.Dns("AA:BB:CC:00:00:01", "192.168.30.1");                                      // likewise the first DNS server
        traffic.Dns("AA:BB:CC:00:00:02", "8.8.4.4");                                           // and a new one
        traffic.Dns("AA:BB:CC:00:00:01", "8.8.8.8");                                           // a device changing to another
        Assert.Equal(3, score.Gather().SecurityAlerts);
        Assert.Equal(3, traffic.Alerts().Count);

        var s = score.Now();
        Assert.Contains(s.Reasons, r => r.Text == "3 security alerts this week" && r.Points == 24);
    }

    [Fact]
    public void An_alert_older_than_a_week_no_longer_counts_and_clearing_the_alerts_gives_the_points_back()
    {
        using var app = new BamfApp();
        var score = app.Services.GetRequiredService<HealthScore>();
        var traffic = app.Services.GetRequiredService<TrafficMonitor>();
        traffic.NoteDhcpServer("192.168.30.1", null, "192.168.30.0/24");
        traffic.NoteDhcpServer("192.168.30.66", null, "192.168.30.0/24");
        Assert.Equal(1, score.Gather().SecurityAlerts);
        Assert.Equal(0, score.Gather(DateTime.UtcNow.AddDays(8)).SecurityAlerts);

        traffic.ClearAlerts();
        Assert.Equal(0, score.Gather().SecurityAlerts);
    }

    [Fact]
    public void A_new_ipv6_router_and_dhcp_going_quiet_count_but_dhcp_answering_again_does_not()
    {
        using var app = new BamfApp();
        var score = app.Services.GetRequiredService<HealthScore>();
        var traffic = app.Services.GetRequiredService<TrafficMonitor>();
        traffic.RouterAdvertHeard("AA:BB:CC:00:00:01", "fe80::1", 1800);          // learned quietly
        traffic.RouterAdvertHeard("AA:BB:CC:00:00:66", "fe80::66", 1800);         // a new one alerts
        Assert.Equal(1, score.Gather().SecurityAlerts);

        traffic.Notify("dhcp", "No DHCP server is answering on 192.168.30.0/24", "x", "silent:192.168.30.0/24");
        Assert.Equal(2, score.Gather().SecurityAlerts);
        traffic.Notify("dhcp-up", "DHCP is answering again on 192.168.30.0/24", "x", "back:192.168.30.0/24");
        Assert.Equal(2, score.Gather().SecurityAlerts);
    }
}
