using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BAMF.Tests;

/// <summary>
/// BAMF itself, running in memory on a database of its own, for the endpoint
/// tests. Its background work (the scanner, the speed test, MQTT and the rest)
/// isn't started, so nothing is scanned or sent anywhere.
/// </summary>
public sealed class BamfApp : WebApplicationFactory<Program>
{
    public string Dir { get; }
    private readonly bool _owns;
    private readonly Dictionary<string, string> _settings;

    /// <summary>
    /// A new install; or, given another's folder, the same install started
    /// again. <paramref name="settings"/> go on top, as appsettings.json would.
    /// </summary>
    public BamfApp(string? dir = null, Dictionary<string, string>? settings = null)
    {
        _owns = dir is null;
        _settings = settings ?? [];
        Dir = dir ?? Path.Combine(Path.GetTempPath(), "bamf-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Bamf:DatabasePath", Path.Combine(Dir, "bamf.db"));
        builder.UseSetting("Bamf:ThemesPath", Path.Combine(Dir, "themes"));
        builder.UseSetting("Bamf:Subnets:0", "192.168.30.0/24");
        builder.UseSetting("Bamf:MdnsListen", "false");
        builder.UseSetting("Bamf:UpdateCheck", "false");
        builder.UseSetting("Bamf:AutoDownloadOui", "false");
        builder.UseSetting("Bamf:ActiveArpScan", "false");
        builder.UseSetting("Bamf:WebhookUrl", "");
        foreach (var (k, v) in _settings) builder.UseSetting(k, v);
        builder.ConfigureTestServices(services =>
        {
            // BAMF's own services are added by a factory; the web host's own isn't.
            foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null).ToList())
                services.Remove(d);
        });
    }

    /// <summary>A client that doesn't follow redirects, so a sign-in redirect can be seen.</summary>
    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public static AuthenticationHeaderValue Basic(string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("bamf:" + password)));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (_owns) try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
