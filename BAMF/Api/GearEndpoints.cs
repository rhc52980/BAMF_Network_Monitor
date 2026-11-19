using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>
/// Network gear: the switches (Map, Wi-Fi, health, networks read from the gateway, gateway events), where a UniFi
/// controller is (the login is kept, never shown again), and what each source last said. Everything off until switched on.
/// </summary>
internal static class GearEndpoints
{
    private static DateTime _lastRun = DateTime.MinValue;

    private static object Json(HostStore store, NetworkGear gear)
    {
        var hosts = store.GetAll().Where(h => !h.Forgotten).ToList();
        var byMac = hosts.Where(h => h.Mac != "").ToDictionary(h => h.Mac.ToUpperInvariant(), h => h);
        var byId = hosts.ToDictionary(h => h.Id);
        string Name(HostRecord h) => h.CustomName != "" ? h.CustomName : h.Hostname is { Length: > 0 } and not "—" ? h.Hostname : h.Ip;
        var now = DateTime.UtcNow;
        var uni = gear.UniFiSettings();
        var autos = store.GetAutoSwitchSources();
        var kinds = store.GetSwitches().ToDictionary(s => s.Id, s => s.Kind);

        // A gateway's LAN interface has its own MAC, so its gear isn't found by MAC: the Map's record of it says which host it is.
        var drawnHost = store.GetAutoSwitches().Where(a => a.Source == "unifi")
            .Join(store.GetSwitches().Where(s => s.HostId > 0), a => a.Id, s => s.Id, (a, s) => (a.Key, s.HostId))
            .GroupBy(x => x.Key.ToUpperInvariant()).ToDictionary(x => x.Key, x => x.First().HostId);

        var rows = store.GetGear().Select(g =>
        {
            byMac.TryGetValue(g.Mac.ToUpperInvariant(), out var h);
            var issues = NetworkGear.IssuesFor(g, now);
            return new
            {
                mac = g.Mac, hostId = h?.Id ?? drawnHost.GetValueOrDefault(g.Mac.ToUpperInvariant()), name = g.Name, kind = g.Kind, model = g.Model, version = g.Version, upgradeTo = g.UpgradeTo,
                uptimeSeconds = g.UptimeSeconds, online = g.Online, ip = g.Ip, ports = g.PortCount, source = g.Source,
                cpu = g.Cpu, mem = g.Mem, temp = g.TempC, poe = g.PoeWatts, poeBudget = g.PoeBudget,
                portsUp = g.Ports.Count(p => p.Up), reboots = g.Reboots, lastReboot = g.LastReboot, updatedAt = g.UpdatedAt,
                ssids = g.Ssids.Select(s => new { name = s.Name, bands = s.Bands }),
                issues = issues.Select(i => new { level = i.Level, text = i.Text }),
                attention = issues.Count(i => i.Level == "warn"),
            };
        }).ToList();

        var wifi = store.GetWifi().Where(w => byId.ContainsKey(w.Key)).Select(w =>
        {
            var l = w.Value;
            var h = byId[w.Key];
            return new
            {
                hostId = l.HostId, name = Name(h), online = h.Online, ap = l.ApName, ssid = l.Ssid, band = l.Band, channel = l.Channel,
                signal = l.Signal, quality = WifiQuality.Label(l.Signal), weak = WifiQuality.Weak(l.Signal), noise = l.Noise, tx = l.TxMbps, rx = l.RxMbps,
                satisfaction = l.Satisfaction, roams = l.Roams, since = l.Since, updatedAt = l.UpdatedAt, movedFrom = l.MovedFrom, movedAt = l.MovedAt,
            };
        }).OrderBy(w => w.signal ?? 0).ToList();

        return new
        {
            features = new { map = store.GearMapOn, wifi = store.GearWifiOn, health = store.GearHealthOn, vlans = store.GearVlansOn, events = store.GearEventsOn },
            // Networks BAMF doesn't scan, read from the gateway; and what the gateway's own events are doing.
            vlans = new
            {
                networks = gear.VlanSummary().Select(n => new { network = n.Network, devices = n.Devices, online = n.Online }),
                suggestions = store.GearVlansOn ? gear.Suggestions().Select(n => new { network = n.Network, devices = n.Devices }).ToList() : [],
                max = HostStore.MaxGatewayNetworks,
            },
            events = new
            {
                threats = store.GearEventThreats, wan = store.GearEventWan, opnsense = gear.OpnSenseSettings() is not null,
                recent = store.GetGearEventLog().Select(e => new { at = e.At, kind = e.Kind, title = e.Title }),
            },
            unifi = new
            {
                configured = uni is not null, fromRouterImport = uni?.FromRouterImport ?? false,
                url = uni?.Settings.Url ?? "", user = uni?.Settings.User ?? "", hasSecret = (uni?.Settings.Secret ?? "") != "",
                site = uni?.Settings.Site ?? "", verify = uni?.Settings.Verify ?? false,
            },
            snmpSwitches = store.GetSnmpConfigs().Count(c => c.Enabled),
            sources = gear.Statuses().Select(s => new { id = s.Id, name = s.Name, state = s.State, detail = s.Detail, at = s.At }),
            drawn = new { gear = autos.Count(a => kinds.GetValueOrDefault(a.Key) != "ssid"), networks = autos.Count(a => kinds.GetValueOrDefault(a.Key) == "ssid"), placements = store.CountAutoPlacements() },
            gear = rows,
            wifi,
        };
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/gear", (HostStore store, NetworkGear gear) => Results.Json(Json(store, gear)));

        // Any of it can be left out. The password is only ever sent in; "" forgets it. Switching a feature on reads
        // at once, so there is an answer to show; switching it off takes back what it made.
        app.MapPost("/api/settings/gear", async (GearRequest body, HostStore store, NetworkGear gear, ScannerService scanner, CancellationToken ct) =>
        {
            if (body.UnifiUrl is { } u)
            {
                if (u.Trim() == "")
                {
                    store.SetSetting("gearUnifiUrl", ""); store.SetSetting("gearUnifiUser", ""); store.SetSetting("gearUnifiSecret", "");
                    store.SetSetting("gearUnifiSite", ""); store.SetSetting("gearUnifiVerify", "false");
                    // What it drew, read and recorded goes with its login.
                    await gear.TakeBackUniFi(ct);
                }
                else
                {
                    var (clean, error) = CleanUrl(u);
                    if (clean is null) return Results.BadRequest(new { error });
                    store.SetSetting("gearUnifiUrl", clean);
                }
            }
            if (body.UnifiUser is { } user) store.SetSetting("gearUnifiUser", user.Trim().Length > 100 ? user.Trim()[..100] : user.Trim());
            // Forgetting the controller already cleared its password; otherwise a password sent in is kept.
            if (body.UnifiSecret is { } secret && !(body.UnifiUrl is { } forgot && forgot.Trim() == ""))
                store.SetSetting("gearUnifiSecret", secret.Length > 200 ? secret[..200] : secret);
            if (body.UnifiSite is { } site) store.SetSetting("gearUnifiSite", site.Trim().Length > 60 ? site.Trim()[..60] : site.Trim());
            if (body.UnifiVerify is { } verify) store.SetSetting("gearUnifiVerify", verify ? "true" : "false");

            // The networks to read from the gateway: only private ones, and never one BAMF scans itself, which would be read twice.
            if (body.VlanNetworks is { } wanted)
            {
                var scanned = scanner.CurrentNetworks();
                var clean = new List<string>();
                foreach (var raw in wanted)
                {
                    if (ScannerService.CheckNetwork(raw, out var label) is { } why) return Results.BadRequest(new { error = why });
                    var address = label.Split('/')[0];
                    if (scanned.Any(n => ScannerService.NetworkContains(n, address) || ScannerService.NetworkContains(label, n.Split('/')[0])))
                        return Results.BadRequest(new { error = $"{label} overlaps a network BAMF scans itself. A network is scanned or read from the gateway, not both." });
                    if (!clean.Contains(label)) clean.Add(label);
                }
                if (clean.Count > HostStore.MaxGatewayNetworks) return Results.BadRequest(new { error = $"At most {HostStore.MaxGatewayNetworks} networks." });
                // A network taken off the list is no longer read, so its devices are hidden (forgotten, not deleted).
                store.ForgetGatewayHosts(store.GearVlanNetworkList().Except(clean).ToList());
                store.SetGearVlanNetworks(clean);
            }
            var readNow = false;
            foreach (var (key, kind, on) in new[] { ("gearEventThreats", "threats", body.EventThreats), ("gearEventWan", "wan", body.EventWan) })
            {
                if (on is not { } want) continue;
                var was = store.GetSetting(key) == "true";
                store.SetSetting(key, want ? "true" : "false");
                if (!want && was) gear.ForgetEventState(kind);      // switched on again later, it starts from then, not from a backlog
                if (want && !was) readNow = true;                  // so the point it starts from is now, not a minute from now
            }

            var turnedOn = false;
            foreach (var (name, key, on) in new[] { ("map", "gearMap", body.Map), ("wifi", "gearWifi", body.Wifi), ("health", "gearHealth", body.Health), ("vlans", "gearVlans", body.Vlans), ("events", "gearEvents", body.Events) })
            {
                if (on is not { } want) continue;
                var was = store.GetSetting(key) == "true";
                store.SetSetting(key, want ? "true" : "false");
                if (want && !was) turnedOn = true;
                if (!want && was) await gear.TakeBack(name, ct);
            }
            if (turnedOn || readNow) await gear.Refresh(true, ct);
            return Results.Json(Json(store, gear));
        });

        // Reads now, once every ten seconds at most, so the settings can be tried before a feature is switched on.
        app.MapPost("/api/gear/run", async (HostStore store, NetworkGear gear, CancellationToken ct) =>
        {
            if (!gear.AnyOn) return Results.Json(new { error = "Switch on a feature first." }, statusCode: 409);
            if (DateTime.UtcNow - _lastRun < TimeSpan.FromSeconds(10)) return Results.Json(new { error = "Just asked. Give it a moment." }, statusCode: 429);
            _lastRun = DateTime.UtcNow;
            return await gear.Refresh(true, ct) ? Results.Json(Json(store, gear)) : Results.Json(new { error = "It is already reading." }, statusCode: 409);
        });
    }

    /// <summary>The controller's address as it will be used, or an error to show.</summary>
    internal static (string? Url, string? Error) CleanUrl(string text)
    {
        text = text.Trim().TrimEnd('/');
        if (text.Length > 300 || !Uri.TryCreate(text, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) || u.Host == "")
            return (null, "Enter the address of the controller's web page, like https://192.168.1.1.");
        if (u.UserInfo != "") return (null, "Leave the login out of the address: it goes in the boxes below.");
        // Only the console's own address: the path the web page lives at isn't needed, and a wrong one would break the API paths.
        return ($"{u.Scheme}://{u.Authority}", null);
    }
}

internal sealed record GearRequest(bool? Map, bool? Wifi, bool? Health, string? UnifiUrl, string? UnifiUser, string? UnifiSecret, string? UnifiSite, bool? UnifiVerify,
    bool? Vlans = null, List<string>? VlanNetworks = null, bool? Events = null, bool? EventThreats = null, bool? EventWan = null);
