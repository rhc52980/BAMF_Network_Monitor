using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LanWatch.Services;

/// <summary>
/// One-click HTTPS: a self-signed certificate made under Settings → Security,
/// kept beside the database as bamf-https.pfx. While it's there, BAMF also
/// listens for HTTPS on Bamf:HttpsPort (8843) from its next start, alongside
/// plain HTTP, which scripts, other BAMF servers and Home Assistant keep using.
/// Nothing vouches for a self-signed certificate, so each browser warns once.
/// </summary>
public static class HttpsCert
{
    public const int DefaultPort = 8843;
    public const string FileName = "bamf-https.pfx";

    // How this start went, for Settings to show.
    public static bool FileHttps { get; set; }          // appsettings.json already serves HTTPS
    public static string? RunningThumbprint { get; set; } // the certificate being served, if any
    public static string? StartProblem { get; set; }      // why a certificate there isn't being served

    /// <summary>Beside the database, wherever that is.</summary>
    public static string PathFor(IConfiguration config)
    {
        var db = config["Bamf:DatabasePath"] ?? "bamf.db";
        if (!Path.IsPathRooted(db)) db = Path.Combine(AppContext.BaseDirectory, db);
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db)) ?? AppContext.BaseDirectory, FileName);
    }

    public static X509Certificate2? Load(string path)
    {
        try { return File.Exists(path) ? X509CertificateLoader.LoadPkcs12FromFile(path, null) : null; }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// A certificate for these names and addresses. 825 days, the longest
    /// Apple's devices accept for a server certificate.
    /// </summary>
    public static X509Certificate2 Create(IReadOnlyList<string> names, IReadOnlyList<IPAddress> addresses, DateTimeOffset now)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest(new X500DistinguishedName("CN=" + (names.FirstOrDefault() ?? "BAMF") + ", O=BAMF"),
            key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var n in names) san.AddDnsName(n);
        foreach (var a in addresses) san.AddIpAddress(a);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server authentication
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(825));
    }

    /// <summary>Written whole or not at all.</summary>
    public static void Save(X509Certificate2 cert, string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, cert.Export(X509ContentType.Pfx));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>What this machine answers to: its name, name.local, localhost, and its addresses.</summary>
    public static (List<string> Names, List<IPAddress> Addresses) LocalNames()
    {
        var host = Environment.MachineName.ToLowerInvariant();
        var names = new List<string> { host, host + ".local", "localhost" };
        try
        {
            var fqdn = Dns.GetHostEntry("").HostName.ToLowerInvariant();
            if (fqdn.Contains('.') && !names.Contains(fqdn)) names.Add(fqdn);
        }
        catch (SocketException) { }
        var addresses = new List<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
                if (u.Address.AddressFamily == AddressFamily.InterNetwork && !addresses.Contains(u.Address)) addresses.Add(u.Address);
        }
        return (names, addresses);
    }

    public static List<string> NamesIn(X509Certificate2 cert)
    {
        var ext = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (ext is null) return [];
        return [.. ext.EnumerateDnsNames(), .. ext.EnumerateIPAddresses().Select(a => a.ToString())];
    }

    /// <summary>Whether the port can be listened on, so a taken one can't stop BAMF starting.</summary>
    public static bool PortFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Any, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    /// <summary>What to do to restart BAMF, for however it's running.</summary>
    public static string RestartHint(bool addon, int port)
    {
        if (addon) return "Restart the add-on in Home Assistant.";
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
            return $"Restart the container. Unless it uses host networking, publish the port too (-p {port}:{port}).";
        if (OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
            return "Restart the BAMF service: Restart-Service BAMF in an administrator PowerShell, or from Services.";
        if (Microsoft.Extensions.Hosting.Systemd.SystemdHelpers.IsSystemdService())
            return "Restart the service: sudo systemctl restart bamf.";
        return "Stop BAMF and start it again.";
    }
}
