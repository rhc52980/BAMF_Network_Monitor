using LanWatch.Services;
using static LanWatch.Api.ApiHelpers;

namespace LanWatch.Api;

/// <summary>The security checks: GreyNoise, the ARP, certificate and IPv6 watches, trusted DHCP and DNS servers.</summary>
internal static class SecurityEndpoints
{
    public static void Map(WebApplication app)
    {
        // Trust a DHCP or DNS server (so it never alerts), or forget it (so it's new again).
        app.MapPost("/api/traffic/trust", (TrustRequest body, ScannerService scanner) =>
        {
            var kind = (body.Kind ?? "").ToLowerInvariant();
            if (kind is not ("dhcp" or "dns") || !System.Net.IPAddress.TryParse(body.Ip ?? "", out _))
                return Results.BadRequest(new { error = "Say which DHCP or DNS server." });
            scanner.Traffic.Trust(kind, body.Ip!, body.Trusted);
            return Results.Ok();
        });

        // The security watch: the ARP watch (conflicts, the gateway's MAC), the
        // certificates on devices' HTTPS ports, and routers that answer UPnP.
        app.MapPost("/api/settings/arp-watch", (ActiveArpRequest body, HostStore store, ScannerService scanner) =>
        {
            store.SetSetting("arpWatch", body.Enabled ? "true" : "false");
            scanner.ForgetArpWatchCache();
            return Results.Ok();
        });

        app.MapPost("/api/settings/cert-watch", (ActiveArpRequest body, HostStore store) =>
        {
            store.SetSetting("certWatch", body.Enabled ? "true" : "false");
            return Results.Ok();
        });

        app.MapPost("/api/settings/ipv6-watch", (ActiveArpRequest body, HostStore store) =>
        {
            store.SetSetting("ipv6Watch", body.Enabled ? "true" : "false");
            return Results.Ok();
        });

        // GreyNoise: has this network's public address been seen scanning the internet?
        // Off unless switched on; turning it on checks straight away.
        app.MapGet("/api/greynoise", (GreyNoiseCheck greynoise) => Results.Json(new { enabled = greynoise.Enabled, result = greynoise.Last }));

        app.MapPost("/api/settings/greynoise", async (ActiveArpRequest body, HostStore store, GreyNoiseCheck greynoise, CancellationToken ct) =>
        {
            store.SetSetting("greynoise", body.Enabled ? "true" : "false");
            if (body.Enabled) await greynoise.Check(ct);
            return Results.Json(new { enabled = greynoise.Enabled, result = greynoise.Last });
        });

        app.MapPost("/api/greynoise/check", async (GreyNoiseCheck greynoise, CancellationToken ct) =>
            greynoise.Enabled ? Results.Json(new { enabled = true, result = await greynoise.Check(ct) })
                              : Results.Json(new { error = "The GreyNoise check is off: switch it on in Settings first." }, statusCode: 409));

        // The DNS watch: is this network's resolver telling the truth? Off unless switched on; turning it on checks straight away.
        app.MapGet("/api/dns", (DnsWatch dns) => Results.Json(new { enabled = dns.Enabled, result = dns.Last, canaries = DnsWatch.Canaries }));

        app.MapPost("/api/settings/dns-watch", async (ActiveArpRequest body, HostStore store, DnsWatch dns, CancellationToken ct) =>
        {
            store.SetSetting("dnsWatch", body.Enabled ? "true" : "false");
            if (body.Enabled) await dns.Check(ct);
            return Results.Json(new { enabled = dns.Enabled, result = dns.Last });
        });

        app.MapPost("/api/dns/check", async (DnsWatch dns, CancellationToken ct) =>
            dns.Enabled ? Results.Json(new { enabled = true, result = await dns.Check(ct) ?? dns.Last })
                        : Results.Json(new { error = "The DNS watch is off: switch it on in Settings first." }, statusCode: 409));

        app.MapGet("/api/security", (HostStore store, ScannerService scanner, SecurityCheck security, DnsWatch dns) => Results.Json(SecurityJson(store, scanner, security, dns)));

        // Check now: the common ports of every online known device, a UPnP search and
        // every HTTPS certificate, one after the other. About a minute on a home network.
        app.MapPost("/api/security/check", async (HostStore store, ScannerService scanner, SecurityCheck security, RuleService rules, GreyNoiseCheck greynoise, DnsWatch dns, CancellationToken ct) =>
        {
            var ran = await security.Run(async c => await rules.PortWatch(c), certs: true, upnp: true, ct);
            if (ran && greynoise.Enabled) await greynoise.Check(ct);
            if (ran && dns.Enabled) await dns.Check(ct);
            return ran ? Results.Json(SecurityJson(store, scanner, security, dns))
                       : Results.Conflict(new { error = "A check is already running." });
        });
    }
}
