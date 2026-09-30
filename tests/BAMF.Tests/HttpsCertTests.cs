using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using LanWatch.Services;
using Microsoft.Extensions.Configuration;

namespace BAMF.Tests;

/// <summary>
/// The certificate Settings → Security makes for one-click HTTPS, and the
/// checks that keep a bad one from stopping BAMF starting.
/// </summary>
public class HttpsCertTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bamf-https-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    public HttpsCertTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static X509Certificate2 Make() =>
        HttpsCert.Create(["server", "server.local", "localhost"], [IPAddress.Parse("192.168.30.5"), IPAddress.Loopback], Now);

    [Fact]
    public void It_covers_the_machines_names_and_addresses()
    {
        using var cert = Make();
        Assert.Equal(["server", "server.local", "localhost", "192.168.30.5", "127.0.0.1"], HttpsCert.NamesIn(cert));
        Assert.Contains("CN=server", cert.Subject);
    }

    [Fact]
    public void It_is_a_server_certificate_not_an_authority()
    {
        using var cert = Make();
        Assert.False(cert.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.Contains(cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(),
            o => o.Value == "1.3.6.1.5.5.7.3.1");
        Assert.True(cert.HasPrivateKey);
    }

    [Fact]
    public void It_lasts_no_longer_than_apple_devices_accept()
    {
        using var cert = Make();
        Assert.True((cert.NotAfter - cert.NotBefore).TotalDays <= 825.01);
        Assert.True(cert.NotBefore.ToUniversalTime() <= Now.UtcDateTime);
    }

    [Fact]
    public void Saved_it_loads_back_with_its_key()
    {
        var path = Path.Combine(_dir, HttpsCert.FileName);
        using var cert = Make();
        HttpsCert.Save(cert, path);
        using var loaded = HttpsCert.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void A_missing_or_unreadable_file_is_no_certificate_rather_than_a_crash()
    {
        var path = Path.Combine(_dir, HttpsCert.FileName);
        Assert.Null(HttpsCert.Load(path));
        File.WriteAllText(path, "not a certificate");
        Assert.Null(HttpsCert.Load(path));
    }

    [Theory]
    [InlineData("/data/bamf.db", "/data")]
    [InlineData("bamf.db", null)]   // beside the exe
    public void It_lives_beside_the_database(string db, string? dir)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bamf:DatabasePath"] = db }).Build();
        var expected = Path.Combine(Path.GetFullPath(dir ?? AppContext.BaseDirectory), HttpsCert.FileName);
        Assert.Equal(expected, HttpsCert.PathFor(config));
    }

    [Fact]
    public void A_port_in_use_is_seen_before_BAMF_tries_to_listen_on_it()
    {
        var l = new TcpListener(IPAddress.Any, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        try { Assert.False(HttpsCert.PortFree(port)); }
        finally { l.Stop(); }
        Assert.True(HttpsCert.PortFree(port));
    }
}
