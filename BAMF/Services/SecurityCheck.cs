using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// Two checks behind the hygiene card.
///
/// Certificates: for every HTTPS port BAMF has found open on a device, it
/// opens a TLS connection and reads the certificate (who it's for, who
/// issued it, when it expires), trusting anything, because it reads the
/// certificate rather than relying on it. It warns 14 days and 3 days before
/// one expires, and when it has. Only ports a scan already found open are
/// touched.
///
/// UPnP: one SSDP search for an Internet Gateway Device on each network BAMF
/// scans. A router that answers lets any device on the network open ports to
/// the internet without asking. This sends a query, so it only runs when
/// someone presses Check now, or daily with the port watch, which is already
/// active scanning the user switched on.
/// </summary>
public sealed class SecurityCheck
{
    public static readonly int[] TlsPorts = { 443, 8443, 5001, 9443, 10443, 4443 };
    public sealed record UpnpDevice(string Ip, string Server, string Location, long? HostId);
    public sealed record UpnpResult(string CheckedAt, List<UpnpDevice> Devices);

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly ILogger<SecurityCheck> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);

    public SecurityCheck(HostStore store, ScannerService scanner, ILogger<SecurityCheck> log)
    {
        _store = store; _scanner = scanner; _log = log;
    }

    public bool CertWatchEnabled => _store.GetSetting("certWatch") != "false";
    public string? LastCheck => _store.GetSetting("securityChecked");
    public bool Busy => _busy.CurrentCount == 0;

    public UpnpResult? Upnp
    {
        get
        {
            try { return JsonSerializer.Deserialize<UpnpResult>(_store.GetSetting("upnpIgd") ?? "null"); }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>
    /// Runs the given checks, one run at a time. False if a run was already going.
    /// portScan runs before the others so they see the ports it finds.
    /// </summary>
    public async Task<bool> Run(Func<CancellationToken, Task>? portScan, bool certs, bool upnp, CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct)) return false;
        try
        {
            if (portScan is not null) await portScan(ct);
            if (upnp) await CheckUpnp(ct);
            if (certs) await CheckCerts(ct);
            _store.SetSetting("securityChecked", DateTime.UtcNow.ToString("o"));
            return true;
        }
        finally { _busy.Release(); }
    }

    // ------------------------------------------------------------ certificates

    public async Task<int> CheckCerts(CancellationToken ct)
    {
        var open = _store.OpenPorts();
        var hosts = _store.GetAll().Where(h => h.Online && !h.Ignored && !h.Forgotten).ToDictionary(h => h.Id);
        var targets = open.SelectMany(kv => kv.Value.Where(p => TlsPorts.Contains(p)).Select(p => (Id: kv.Key, Port: p)))
            .Where(t => hosts.ContainsKey(t.Id)).ToList();
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(targets.Select(async t =>
        {
            await gate.WaitAsync(ct);
            try { await CheckOne(hosts[t.Id], t.Port, ct); }
            finally { gate.Release(); }
        }));
        _log.LogInformation("Certificate check read {Count} HTTPS port(s)", targets.Count);
        return targets.Count;
    }

    private async Task CheckOne(HostRecord h, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(h.Ip, port, timeout.Token);
            X509Certificate2? cert = null;
            await using var ssl = new SslStream(tcp.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = h.Ip,
                // Read it, don't rely on it: a LAN device's certificate is often
                // self-signed, and that's exactly the one worth looking at.
                RemoteCertificateValidationCallback = (_, c, _, _) => { if (c is not null) cert = new X509Certificate2(c); return true; },
            }, timeout.Token);
            if (cert is null) { _store.SaveCertError(h.Id, port, "No certificate offered."); return; }
            var selfSigned = cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData);
            var notAfter = cert.NotAfter.ToUniversalTime();
            _store.SaveCert(h.Id, port, CommonName(cert.Subject), CommonName(cert.Issuer), notAfter.ToString("o"), selfSigned);
            await MaybeAlert(h, port, CommonName(cert.Subject), notAfter, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _store.SaveCertError(h.Id, port, "Timed out.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _store.SaveCertError(h.Id, port, ex.GetBaseException().Message);
        }
    }

    /// <summary>One alert per stage (14 days, 3 days, expired) per certificate.</summary>
    private async Task MaybeAlert(HostRecord h, int port, string subject, DateTime notAfter, CancellationToken ct)
    {
        var days = (notAfter - DateTime.UtcNow).TotalDays;
        var stage = days < 0 ? "expired" : days <= 3 ? "3" : days <= 14 ? "14" : "";
        if (stage == "") return;
        var row = _store.GetCerts().FirstOrDefault(c => c.HostId == h.Id && c.Port == port);
        if (row is null || row.Alerted == stage) return;
        _store.MarkCertAlerted(h.Id, port, stage);
        var name = h.CustomName != "" ? h.CustomName : h.Hostname != "" ? h.Hostname : h.Ip;
        var date = TimeZoneInfo.ConvertTimeFromUtc(notAfter, TimeZoneInfo.Local).ToString("yyyy-MM-dd");
        var what = subject != "" ? $", for {subject}," : "";
        var (title, detail) = stage == "expired"
            ? ($"Certificate expired on {name}:{port}",
               $"The HTTPS certificate on {name} ({h.Ip}) port {port}{what} expired on {date}. Browsers warn about it, and anything that checks certificates will refuse to connect.")
            : ($"Certificate on {name}:{port} expires in {Math.Max(1, (int)Math.Ceiling(days))} days",
               $"The HTTPS certificate on {name} ({h.Ip}) port {port}{what} expires on {date}. Renew it before then.");
        _store.AddAlert("cert", title, detail);
        await _scanner.SendGenericAlert(title, detail, "cert", ct);
    }

    private static string CommonName(string dn)
    {
        foreach (var part in dn.Split(','))
        {
            var p = part.Trim();
            if (p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return p[3..].Trim();
        }
        return dn.Length > 120 ? dn[..120] : dn;
    }

    // ------------------------------------------------------------ UPnP

    public async Task<UpnpResult> CheckUpnp(CancellationToken ct)
    {
        var found = new Dictionary<string, UpnpDevice>();
        var locals = _scanner.NetworkPlaces().Values.Select(p => p.SelfIp).Where(ip => ip is not null).Distinct().ToList();
        var all = _store.GetAll();
        foreach (var local in locals)
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(local!), 0));
                var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                foreach (var st in new[] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:device:InternetGatewayDevice:2" })
                {
                    var msg = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {st}\r\n\r\n");
                    await udp.SendAsync(msg, target, ct);
                }
                using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
                window.CancelAfter(TimeSpan.FromSeconds(3));
                while (true)
                {
                    UdpReceiveResult r;
                    try { r = await udp.ReceiveAsync(window.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                    var text = Encoding.ASCII.GetString(r.Buffer);
                    var headers = text.Split("\r\n").Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
                        .GroupBy(p => p[0].Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First()[1].Trim());
                    if (!headers.TryGetValue("ST", out var got) || !got.Contains("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase)) continue;
                    var ip = r.RemoteEndPoint.Address.ToString();
                    var host = all.FirstOrDefault(h => h.Ip == ip);
                    found[ip] = new UpnpDevice(ip, headers.GetValueOrDefault("SERVER", ""), headers.GetValueOrDefault("LOCATION", ""), host?.Id);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogInformation(ex, "UPnP search from {Local} failed", local); }
        }
        var result = new UpnpResult(DateTime.UtcNow.ToString("o"), found.Values.ToList());
        _store.SetSetting("upnpIgd", JsonSerializer.Serialize(result));
        _log.LogInformation("UPnP search: {Count} internet gateway device(s) answered", found.Count);
        return result;
    }
}
