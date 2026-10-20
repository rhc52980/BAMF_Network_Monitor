using LanWatch.Services;

namespace BAMF.Tests;

/// <summary>
/// Which networks Settings can add. Only private ones no bigger than a sweep
/// covers, so whoever can open the dashboard can't point BAMF, or the port
/// scans that follow its networks, anywhere else.
/// </summary>
public class NetworkRulesTests
{
    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.0/24")]
    [InlineData("192.168.1.77/24", "192.168.1.0/24")]      // tidied to the network's own address
    [InlineData(" 10.0.4.0/22 ", "10.0.4.0/22")]
    [InlineData("10.0.8.0/21", "10.0.8.0/21")]              // the widest a sweep covers: 2,046 addresses
    [InlineData("10.0.9.7/21", "10.0.8.0/21")]
    [InlineData("172.16.0.0/24", "172.16.0.0/24")]
    [InlineData("172.31.255.0/24", "172.31.255.0/24")]
    [InlineData("100.64.1.0/24", "100.64.1.0/24")]          // carrier-grade NAT, as Tailscale uses
    [InlineData("169.254.0.0/24", "169.254.0.0/24")]        // link-local
    [InlineData("192.168.5.4/30", "192.168.5.4/30")]
    public void A_private_network_is_taken(string input, string label)
    {
        Assert.Null(ScannerService.CheckNetwork(input, out var got));
        Assert.Equal(label, got);
    }

    [Theory]
    [InlineData("8.8.8.0/24")]
    [InlineData("1.1.1.0/24")]
    [InlineData("172.32.0.0/24")]        // just past 172.16/12
    [InlineData("172.15.0.0/24")]
    [InlineData("100.128.0.0/24")]       // just past 100.64/10
    [InlineData("11.0.0.0/24")]
    public void A_public_network_is_refused(string input)
    {
        Assert.Contains("private networks", ScannerService.CheckNetwork(input, out var label));
        Assert.Equal("", label);
    }

    [Theory]
    [InlineData("192.168.0.0/16")]
    [InlineData("10.0.0.0/8")]
    [InlineData("10.0.0.0/20")]
    [InlineData("0.0.0.0/0")]
    public void A_network_bigger_than_a_sweep_is_refused(string input)
    {
        Assert.Contains("bigger than BAMF sweeps", ScannerService.CheckNetwork(input, out _));
    }

    [Theory]
    [InlineData("10.0.8.0", 24, 254, "10.0.8.1", "10.0.8.254")]
    [InlineData("10.0.8.0", 22, 1022, "10.0.8.1", "10.0.11.254")]
    [InlineData("10.0.8.0", 21, 2046, "10.0.8.1", "10.0.15.254")]          // the whole of a /21 is swept
    [InlineData("10.0.0.0", 20, 2046, "10.0.0.1", "10.0.7.254")]           // anything wider: its first /21's worth
    [InlineData("10.0.0.0", 16, 2046, "10.0.0.1", "10.0.7.254")]
    public void A_sweep_covers_every_address_of_a_network_up_to_a_slash_21_and_no_more(string net, int prefix, int count, string first, string last)
    {
        var all = ScannerService.EnumerateSubnet(System.Net.IPAddress.Parse(net), prefix).Select(a => a.ToString()).ToList();
        Assert.Equal(count, all.Count);
        Assert.Equal(first, all[0]);
        Assert.Equal(last, all[^1]);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.DoesNotContain(net, all);                                                  // never the network address itself
    }

    [Fact]
    public void The_neighbour_table_limit_is_a_number_where_it_can_be_read_and_nothing_where_it_cannot()
    {
        var limit = ScannerService.NeighbourLimit();
        if (OperatingSystem.IsLinux()) Assert.True(limit is null or > 0);
        else Assert.Null(limit);
    }

    [Fact]
    public void The_widest_network_is_a_slash_21_and_the_refusal_says_how_many_addresses_that_is()
    {
        Assert.Equal(21, ScannerService.WidestPrefix);
        Assert.Equal(2046, ScannerService.WidestAddresses);
        Assert.Contains("2,046 addresses", ScannerService.CheckNetwork("10.0.0.0/20", out _));
        Assert.Contains("Use /21 or smaller", ScannerService.CheckNetwork("10.0.0.0/20", out _));
    }

    [Theory]
    [InlineData("10.1.2.0/31")]
    [InlineData("10.1.2.3/32")]
    public void A_network_with_no_room_for_devices_is_refused(string input)
    {
        Assert.Contains("no room for devices", ScannerService.CheckNetwork(input, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("banana")]
    [InlineData("192.168.1.0")]
    [InlineData("192.168.1.0/")]
    [InlineData("192.168.1.0/24/8")]
    [InlineData("192.168.1.0/33")]
    [InlineData("fd00::/64")]
    [InlineData("192.168.300.0/24")]
    public void Anything_that_isnt_a_network_is_refused(string? input)
    {
        Assert.Contains("isn't a network", ScannerService.CheckNetwork(input, out _));
    }
}
