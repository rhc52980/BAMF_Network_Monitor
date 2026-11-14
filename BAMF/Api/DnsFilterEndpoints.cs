using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>The Pi-hole and AdGuard Home reader: its switch, its address and login (kept, never shown again), and reading now.</summary>
internal static class DnsFilterEndpoints
{
    private static DateTime _lastRun = DateTime.MinValue;

    private static object Json(DnsFilterReader r, HostStore store)
    {
        var last = r.Last;
        var hosts = store.GetAll();
        object? shown = last is null ? null : new
        {
            at = last.At, provider = last.Provider, total = last.Total, blocked = last.Blocked, percent = last.Percent, error = last.Error,
            clients = last.Clients.Select(c =>
            {
                var h = hosts.FirstOrDefault(x => x.Ip == c.Ip);
                var name = h is null ? "" : h.CustomName != "" ? h.CustomName : h.Hostname is { Length: > 0 } and not "—" ? h.Hostname : "";
                return new { ip = c.Ip, name = name != "" ? name : c.Name, hostId = h?.Id, queries = c.Queries, blocked = c.Blocked };
            }).ToList(),
        };
        return new { enabled = r.Enabled, provider = r.Provider, url = r.Url, user = r.User, hasSecret = r.Secret != "", insecure = r.Insecure, last = shown };
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/dns-filter", (DnsFilterReader r, HostStore store) => Results.Json(Json(r, store)));

        // Any of them can be left out. The password is only ever sent in; "" forgets it. Switching on reads at once, so there is an answer to show.
        app.MapPost("/api/settings/dns-filter", async (DnsFilterRequest body, HostStore store, DnsFilterReader r, CancellationToken ct) =>
        {
            if (body.Provider is { } p)
            {
                if (p is not ("pihole" or "adguard")) return Results.BadRequest(new { error = "Pi-hole or AdGuard Home." });
                store.SetSetting("dnsFilter", p);
            }
            if (body.Url is { } u)
            {
                if (u.Trim() == "") { store.SetSetting("dnsFilterUrl", ""); store.SetSetting("dnsFilterOn", "false"); store.SetSetting("dnsFilterSnapshot", ""); }
                else
                {
                    var (clean, error) = DnsFilterReader.CleanUrl(u);
                    if (clean is null) return Results.BadRequest(new { error });
                    store.SetSetting("dnsFilterUrl", clean);
                }
            }
            if (body.User is { } user) store.SetSetting("dnsFilterUser", user.Trim().Length > 100 ? user.Trim()[..100] : user.Trim());
            if (body.Secret is { } secret) store.SetSetting("dnsFilterSecret", secret.Length > 200 ? secret[..200] : secret);
            if (body.Insecure is { } ins) store.SetSetting("dnsFilterInsecure", ins ? "true" : "false");
            if (body.Enabled is { } on)
            {
                if (on && r.Url == "") return Results.BadRequest(new { error = "Give it the address of your Pi-hole or AdGuard Home first." });
                store.SetSetting("dnsFilterOn", on ? "true" : "false");
                if (on) await r.Refresh(ct);
            }
            return Results.Json(Json(r, store));
        });

        // Reads now, whatever the schedule, even with the switch off so the details can be tried first; once every ten seconds at most.
        app.MapPost("/api/dns-filter/run", async (DnsFilterReader r, HostStore store, CancellationToken ct) =>
        {
            if (r.Url == "") return Results.Json(new { error = "Give it the address of your Pi-hole or AdGuard Home first." }, statusCode: 409);
            if (DateTime.UtcNow - _lastRun < TimeSpan.FromSeconds(10)) return Results.Json(new { error = "Just asked. Give it a moment." }, statusCode: 429);
            _lastRun = DateTime.UtcNow;
            var snap = await r.Refresh(ct);
            return snap is null ? Results.Json(new { error = "It is already reading." }, statusCode: 409) : Results.Json(Json(r, store));
        });
    }
}

internal sealed record DnsFilterRequest(bool? Enabled, string? Provider, string? Url, string? User, string? Secret, bool? Insecure);
