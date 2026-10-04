using LanWatch.Services;
using static LanWatch.Api.ApiHelpers;

namespace LanWatch.Api;

/// <summary>The internet watch and the speed test.</summary>
internal static class InternetEndpoints
{
    public static void Map(WebApplication app, string version)
    {
        // The internet watch: is the line out of the house up, and was it earlier?
        app.MapGet("/api/wan", (WanWatch wan, HostStore store) =>
        {
            // The log is what's kept; an outage still running is added on top of it so
            // the card can say "since 14:02" while it's happening.
            var logged = store.GetWanOutageLog(50).Select(o => new { start = o.Start, end = o.End, minutes = o.Minutes, local = o.Local, running = false });
            if (wan.Current is { } now)
                logged = new[] { new { start = now.Since.ToString("o"), end = DateTime.UtcNow.ToString("o"),
                    minutes = Math.Max(1, (int)Math.Round((DateTime.UtcNow - now.Since).TotalMinutes)), local = now.Local, running = true } }.Concat(logged);
            return Results.Json(new
            {
                state = wan.Now(),
                samples = store.GetWanSamples(24).Select(s => new { at = s.At, gateway = s.Gateway, internet = s.Internet }),
                outages = logged,
                slow = SlowSpells(),
                // The home's public address, learned from the speed test or the GreyNoise check; null until one has run.
                externalIp = store.GetExternalIp(),
            });

            // Slow spells the same way: the kept ones, and one still running on top.
            IEnumerable<object> SlowSpells()
            {
                var kept = store.GetWanSlowLog(50).Select(o => (object)new { start = o.Start, end = o.End, minutes = o.Minutes, worst = o.WorstMs, usual = o.UsualMs, local = o.Local, running = false });
                if (wan.CurrentSlow is not { } now) return kept;
                return new[] { (object)new { start = now.Since.ToString("o"), end = DateTime.UtcNow.ToString("o"),
                    minutes = Math.Max(1, (int)Math.Round((DateTime.UtcNow - now.Since).TotalMinutes)), worst = now.Worst, usual = now.Usual, local = now.Local, running = true } }.Concat(kept);
            }
        });

        // "Look it up" on the Network services card: this home's public address, found on demand with one
        // request for a file of no bytes (see ExternalIpLookup). Two presses within a few seconds share an answer.
        var lookupGate = new SemaphoreSlim(1, 1);
        var lastLookup = DateTime.MinValue;
        app.MapPost("/api/externalip/lookup", async (HostStore store, IHttpClientFactory http, CancellationToken ct) =>
        {
            await lookupGate.WaitAsync(ct);
            try
            {
                var changed = false;
                if (DateTime.UtcNow - lastLookup > TimeSpan.FromSeconds(5))
                {
                    var (ip, error) = await ExternalIpLookup.Find(http.CreateClient(), ct);
                    if (error is not null) return Results.Json(new { error }, statusCode: 502);
                    changed = store.RecordExternalIp(ip, "lookup");
                    if (store.GetExternalIp()?.Ip != ip) return Results.Json(new { error = $"Cloudflare answered with {ip}, which isn't a public address." }, statusCode: 502);
                    lastLookup = DateTime.UtcNow;
                }
                return Results.Json(new { externalIp = store.GetExternalIp(), changed });
            }
            finally { lookupGate.Release(); }
        });

        app.MapPost("/api/settings/wanslow",(WanSlowRequest body, HostStore store, WanWatch wan) =>
        {
            var mode = (body.Mode ?? "").Trim().ToLowerInvariant();
            if (mode == "fixed")
            {
                if (body.Ms is not (>= 20 and <= 5000)) return Results.BadRequest(new { error = "Between 20 and 5000 ms." });
                mode = body.Ms.Value.ToString();
            }
            else if (mode is not ("auto" or "off")) return Results.BadRequest(new { error = "Automatic, a set limit, or off." });
            store.SetSetting("wanSlow", mode);
            wan.ResetUsual();
            return Results.Json(new { state = wan.Now() });
        });

        app.MapPost("/api/settings/waninterval", (WanIntervalRequest body, HostStore store, WanWatch wan) =>
        {
            if (body.Seconds is < 20 or > 3600) return Results.BadRequest(new { error = "Between 20 seconds and an hour." });
            store.SetSetting("wanInterval", body.Seconds.ToString());
            return Results.Json(new { seconds = wan.IntervalSeconds });
        });

        app.MapPost("/api/settings/wanwatch", (ActiveArpRequest body, HostStore store, WanWatch wan) =>
        {
            store.SetSetting("wanWatch", body.Enabled ? "true" : "false");
            return Results.Json(new { state = wan.Now() });
        });

        app.MapPost("/api/settings/wantarget", (WanTargetRequest body, HostStore store, WanWatch wan) =>
        {
            var t = (body.Target ?? "").Trim();
            if (!System.Net.IPAddress.TryParse(t, out var ip)) return Results.BadRequest(new { error = "That isn't an address BAMF can ping. Try 8.8.8.8, or your provider's DNS." });
            store.SetSetting("wanTarget", ip.ToString());
            return Results.Json(new { state = wan.Now() });
        });

        // A printable page of what the internet watch and the speed tests recorded, for sending to the provider.
        app.MapGet("/report/internet", (HostStore store, WanWatch wan, int? days) =>
        {
            var d = Math.Clamp(days ?? 30, 1, 365);
            var html = InternetReport.Build(new InternetReport.Input(DateTime.UtcNow, d, version, wan.Target, wan.IntervalSeconds,
                store.GetExternalIp()?.Ip, store.GetWanOutageLog(500), store.GetWanSlowLog(500), store.GetSpeedResults(d)));
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // The speed test: the results, and the one running now if there is one.
        app.MapGet("/api/speedtest", (SpeedTest speed, HostStore store) =>
        {
            var usual = speed.Usual();
            return Results.Json(new
            {
                schedule = speed.Schedule,
                running = speed.Running,
                progress = speed.Now is { } p ? new { phase = p.Phase, mbps = p.Mbps } : null,
                usual = usual is { } u ? new { down = u.Down, up = u.Up } : null,
                results = store.GetSpeedResults(90).Select(r => new
                {
                    at = r.At, down = r.DownMbps, up = r.UpMbps, ping = r.PingMs, jitter = r.JitterMs,
                    server = r.Server, mb = r.MegabytesUsed, manual = r.Manual, error = r.Error,
                }),
            });
        });

        app.MapPost("/api/speedtest/run", (SpeedTest speed) =>
            speed.RunNow() ? Results.Json(new { started = true }) : Results.Conflict(new { error = "A speed test is already running." }));

        app.MapPost("/api/settings/speedtest", (SpeedTestRequest body, HostStore store, SpeedTest speed) =>
        {
            var v = (body.Schedule ?? "").Trim().ToLowerInvariant();
            if (v is not ("off" or "daily" or "6h")) return Results.BadRequest(new { error = "Never, daily or every six hours." });
            store.SetSetting("speedTest", v);
            return Results.Json(new { schedule = speed.Schedule });
        });
    }
}
