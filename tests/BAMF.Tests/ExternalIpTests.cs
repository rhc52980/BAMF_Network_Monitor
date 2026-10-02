using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LanWatch.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BAMF.Tests;

/// <summary>
/// The home's public address, learned from the speed test and the GreyNoise check: which answers are
/// taken as an address, how a change is told from a confirmation, and that /api/wan carries it.
/// </summary>
public class ExternalIpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bamf-extip-" + Guid.NewGuid().ToString("N"));

    public ExternalIpTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private HostStore Store() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Bamf:DatabasePath"] = Path.Combine(_dir, "bamf.db"),
    }).Build());

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("203.0.113.9", true)]
    [InlineData("100.64.1.2", true)]            // a shared (carrier-grade NAT) address is still the one the internet sees
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("192.168.1.20", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.32.0.1", true)]            // just outside 172.16/12
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.3.4", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.251", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    public void Only_a_public_address_counts(string text, bool public_)
    {
        Assert.Equal(public_, HostStore.IsPublicAddress(IPAddress.Parse(text)));
        var store = Store();
        store.RecordExternalIp(text, "speedtest");
        Assert.Equal(public_, store.GetExternalIp() is not null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("1.2.3")]
    public void Anything_else_is_ignored(string? text)
    {
        var store = Store();
        Assert.False(store.RecordExternalIp(text, "greynoise"));
        Assert.Null(store.GetExternalIp());
    }

    [Fact]
    public void The_first_address_is_noted_without_being_a_change()
    {
        var store = Store();
        Assert.False(store.RecordExternalIp("203.0.113.9", "speedtest", new DateTime(2026, 10, 1, 4, 10, 0, DateTimeKind.Utc)));
        var ip = store.GetExternalIp()!;
        Assert.Equal("203.0.113.9", ip.Ip);
        Assert.Equal("speedtest", ip.Source);
        Assert.Equal(ip.At, ip.Since);
        Assert.Null(ip.Previous);
        Assert.Null(ip.ChangedAt);
    }

    [Fact]
    public void The_same_address_again_is_a_confirmation_and_keeps_its_age()
    {
        var store = Store();
        var first = new DateTime(2026, 10, 1, 4, 10, 0, DateTimeKind.Utc);
        var later = first.AddDays(3);
        store.RecordExternalIp("203.0.113.9", "speedtest", first);
        Assert.False(store.RecordExternalIp("203.0.113.9", "greynoise", later));
        var ip = store.GetExternalIp()!;
        Assert.Equal("greynoise", ip.Source);                              // the newest check is the one named
        Assert.Equal(later.ToString("o"), ip.At);
        Assert.Equal(first.ToString("o"), ip.Since);
        Assert.Null(ip.Previous);
    }

    [Fact]
    public void A_different_address_is_a_change_and_remembers_the_old_one()
    {
        var store = Store();
        var first = new DateTime(2026, 10, 1, 4, 10, 0, DateTimeKind.Utc);
        var later = first.AddDays(9);
        store.RecordExternalIp("203.0.113.9", "speedtest", first);
        Assert.True(store.RecordExternalIp("198.51.100.77", "speedtest", later));
        var ip = store.GetExternalIp()!;
        Assert.Equal("198.51.100.77", ip.Ip);
        Assert.Equal("203.0.113.9", ip.Previous);
        Assert.Equal(later.ToString("o"), ip.Since);
        Assert.Equal(later.ToString("o"), ip.ChangedAt);
    }

    [Fact]
    public void A_bad_answer_doesnt_wipe_the_address_already_known()
    {
        var store = Store();
        store.RecordExternalIp("203.0.113.9", "speedtest");
        store.RecordExternalIp("192.168.1.1", "greynoise");
        store.RecordExternalIp("junk", "speedtest");
        Assert.Equal("203.0.113.9", store.GetExternalIp()!.Ip);
    }

    [Fact]
    public async Task The_internet_watchs_answer_carries_the_address()
    {
        using var app = new BamfApp();
        var c = app.Client();
        var empty = await c.GetFromJsonAsync<JsonElement>("/api/wan");
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("externalIp").ValueKind);

        app.Services.GetRequiredService<HostStore>().RecordExternalIp("203.0.113.9", "speedtest");
        var known = (await c.GetFromJsonAsync<JsonElement>("/api/wan")).GetProperty("externalIp");
        Assert.Equal("203.0.113.9", known.GetProperty("ip").GetString());
        Assert.Equal("speedtest", known.GetProperty("source").GetString());
    }
}
