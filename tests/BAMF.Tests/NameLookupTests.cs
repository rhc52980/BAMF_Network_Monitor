using System.Net;
using LanWatch.Services;

namespace BAMF.Tests;

/// <summary>
/// The names looked up after a sweep: a few at a time instead of one after another, each address once, a failure costing only its own name.
/// </summary>
public class NameLookupTests
{
    private static List<IPAddress> Addresses(int n) => Enumerable.Range(1, n).Select(i => IPAddress.Parse($"10.0.{i / 250}.{i % 250 + 1}")).ToList();

    [Fact]
    public async Task Many_addresses_are_looked_up_together_but_never_more_than_the_limit_at_once()
    {
        var now = 0; var peak = 0;
        var names = await ScannerService.ResolveNames(Addresses(80), 16, async (ip, _) =>
        {
            var n = Interlocked.Increment(ref now);
            int p; do { p = Volatile.Read(ref peak); } while (n > p && Interlocked.CompareExchange(ref peak, n, p) != p);
            await Task.Delay(20);
            Interlocked.Decrement(ref now);
            return "host-" + ip.GetAddressBytes()[3];
        }, CancellationToken.None);
        Assert.Equal(80, names.Count);
        Assert.InRange(peak, 2, 16);
        Assert.Equal("host-" + Addresses(80)[5].GetAddressBytes()[3], names[Addresses(80)[5]]);
    }

    [Fact]
    public async Task A_slow_lookup_does_not_make_the_rest_wait_for_it()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var names = await ScannerService.ResolveNames(Addresses(40), ScannerService.NameLookups, async (ip, _) =>
        {
            await Task.Delay(ip.GetAddressBytes()[3] % 2 == 0 ? 200 : 10);       // half of them take their time
            return "x";
        }, CancellationToken.None);
        Assert.Equal(40, names.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), sw.Elapsed.ToString());   // one after another would be about four seconds
    }

    [Fact]
    public async Task An_address_is_looked_up_once_and_a_failure_gives_no_name_without_failing_the_others()
    {
        var calls = new System.Collections.Concurrent.ConcurrentBag<string>();
        var ips = new[] { "10.0.0.1", "10.0.0.2", "10.0.0.2", "10.0.0.3" }.Select(IPAddress.Parse).ToList();
        var names = await ScannerService.ResolveNames(ips, 4, (ip, _) =>
        {
            calls.Add(ip.ToString());
            if (ip.ToString() == "10.0.0.2") throw new InvalidOperationException("no resolver");
            return Task.FromResult("name-" + ip);
        }, CancellationToken.None);
        Assert.Equal(3, calls.Count);
        Assert.Equal("name-10.0.0.1", names[ips[0]]);
        Assert.Equal("", names[ips[1]]);
        Assert.Equal("name-10.0.0.3", names[ips[3]]);
    }

    [Fact]
    public async Task Cancelling_stops_the_lookups()
    {
        using var cts = new CancellationTokenSource();
        var task = ScannerService.ResolveNames(Addresses(40), 4, async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return "x"; }, cts.Token);
        cts.CancelAfter(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}
