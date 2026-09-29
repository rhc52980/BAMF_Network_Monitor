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
    [InlineData("10.0.0.0/21")]
    [InlineData("0.0.0.0/0")]
    public void A_network_bigger_than_a_sweep_is_refused(string input)
    {
        Assert.Contains("bigger than BAMF sweeps", ScannerService.CheckNetwork(input, out _));
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
