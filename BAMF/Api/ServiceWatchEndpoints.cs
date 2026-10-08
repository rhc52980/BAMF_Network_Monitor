using System.Net;
using LanWatch.Services;

namespace LanWatch.Api;

internal sealed record ServiceWatchRequest(long HostId, string? Name, string? Kind, int Port, string? Path, bool Https, int SlowMs);

/// <summary>The service watch: the services being watched with their last day, what BAMF suggests adding, a one-off try, and the switch.</summary>
internal static class ServiceWatchEndpoints
{
    private static readonly SemaphoreSlim Trying = new(1, 1);

    private static string DeviceName(HostRecord h) => h.CustomName != "" ? h.CustomName : !string.IsNullOrEmpty(h.Hostname) && h.Hostname != "—" ? h.Hostname : h.Ip;

    /// <summary>Checks a request and makes it what is stored; the message for the person when it can't be.</summary>
    private static string? Validate(ServiceWatchRequest b, HostStore store, out HostRecord host, out string kind, out string path, out string name)
    {
        host = default!; kind = ""; path = "/"; name = "";
        var h = store.GetAll().FirstOrDefault(x => x.Id == b.HostId && !x.Forgotten);
        if (h is null) return "Pick one of your devices.";
        if (!IPAddress.TryParse(h.Ip, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return "BAMF doesn't have an IPv4 address for that device.";
        host = h;
        kind = (b.Kind ?? "").Trim().ToLowerInvariant();
        if (kind is not ("web" or "port")) return "Check a web page or a port.";
        if (b.Port is < 1 or > 65535) return "Enter a port from 1 to 65535.";
        if (kind == "web")
        {
            path = string.IsNullOrWhiteSpace(b.Path) ? "/" : b.Path.Trim();
            if (!path.StartsWith('/')) path = "/" + path;
            if (path.Length > 200 || path.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return "A path can't have spaces in it, and is at most 200 characters.";
        }
        if (b.SlowMs != 0 && b.SlowMs is < 50 or > 60000) return "Call it slow somewhere from 50 ms to 60 s, or leave it blank.";
        name = (b.Name ?? "").Trim();
        if (name.Length > 60) return "A name is at most 60 characters.";
        if (name.Any(char.IsControl)) return "A name can't have control characters in it.";
        if (name.Length == 0) name = ServiceWatch.NameFor(b.Port, kind);
        return null;
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/service-watch", (HostStore store, ServiceWatch watch) =>
        {
            var rows = watch.Snapshot();
            return Results.Json(new
            {
                enabled = watch.Enabled,
                max = ServiceWatch.Max,
                bucketMinutes = ServiceWatch.BucketMinutes,
                failsToAlert = ServiceWatch.FailsToAlert,
                services = rows,
                suggestions = rows.Count >= ServiceWatch.Max ? Array.Empty<ServiceWatch.Suggestion>() : watch.Suggestions(),
                devices = store.GetAll().Where(h => !h.Forgotten && !h.Ignored && IPAddress.TryParse(h.Ip, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .OrderBy(h => DeviceName(h), StringComparer.OrdinalIgnoreCase).Select(h => new { id = h.Id, name = DeviceName(h), ip = h.Ip }),
            });
        });

        app.MapPost("/api/settings/service-watch", (ActiveArpRequest body, HostStore store) =>
        {
            store.SetSetting("serviceWatch", body.Enabled ? "true" : "false");
            return Results.Json(new { enabled = body.Enabled });
        });

        // One check, now, without saving anything, so a service can be tried before it's watched.
        app.MapPost("/api/service-watch/try", async (ServiceWatchRequest body, HostStore store, ServiceWatch watch, CancellationToken ct) =>
        {
            var bad = Validate(body, store, out var host, out var kind, out var path, out var name);
            if (bad is not null) return Results.BadRequest(new { error = bad });
            if (!await Trying.WaitAsync(0, ct)) return Results.Json(new { error = "A check is already running. Try again in a moment." }, statusCode: 429);
            try
            {
                var probe = new HostStore.WatchedService(0, host.Id, name, kind, body.Port, path, body.Https, body.SlowMs, "");
                var s = await watch.Probe(probe, host.Ip, ct);
                var slow = s.Ok && body.SlowMs > 0 && s.Ms >= body.SlowMs;
                return Results.Json(new
                {
                    ok = s.Ok, ms = s.Ms, slow, error = s.Error, url = kind == "web" ? ServiceWatch.Url(probe, host.Ip) : $"{host.Ip}:{body.Port}",
                    summary = s.Ok ? (kind == "web" ? "Answered normally" : "Accepted a connection") + (s.Ms is { } ms ? $" in {ServiceWatch.FormatMs(ms)}" : "") + (slow ? ", which is over the slow limit" : "")
                        : s.Error ?? "No answer",
                });
            }
            finally { Trying.Release(); }
        });

        app.MapPost("/api/service-watch", (ServiceWatchRequest body, HostStore store, ServiceWatch watch) =>
        {
            var bad = Validate(body, store, out var host, out var kind, out var path, out var name);
            if (bad is not null) return Results.BadRequest(new { error = bad });
            if (store.GetServices().Count >= ServiceWatch.Max) return Results.Conflict(new { error = $"BAMF watches at most {ServiceWatch.Max} services." });
            var added = store.AddService(host.Id, name, kind, body.Port, kind == "web" ? path : "/", kind == "web" && body.Https, body.SlowMs);
            if (added is null) return Results.Conflict(new { error = "That one is already being watched." });
            return Results.Json(new { id = added.Id });
        });

        app.MapDelete("/api/service-watch/{id:long}", (long id, HostStore store) => store.DeleteService(id) ? Results.Ok() : Results.NotFound());
    }
}
