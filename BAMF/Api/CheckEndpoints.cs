using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>
/// Three checks on the shape of the network: the cutover check (a snapshot before a change, compared after), the segmentation check
/// (which devices are on which network, against rules you set) and DHCP pool pressure. The snapshot is a button; the other two are off
/// until switched on, and an alert for each is a second switch, also off.
/// </summary>
internal static class CheckEndpoints
{
    private static object SnapshotJson(HostStore.SnapshotInfo s) => new { id = s.Id, name = s.Name, at = s.At, devices = s.Devices, online = s.Online };

    private static object ItemJson(Cutover.Item i) => new { mac = i.Mac, name = i.Name, ip = i.Ip, network = i.Network, wasIp = i.WasIp, wasNetwork = i.WasNetwork, lastSeen = i.LastSeen };

    private static object SegmentationJson(HostStore store, ScannerService scanner, SegmentationCheck seg)
    {
        var networks = scanner.CurrentNetworks().Concat(store.GearVlanNetworkList()).Concat(store.GetAll().Where(h => !h.Forgotten).Select(h => h.Subnet))
            .Where(n => n != "").Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        return new
        {
            enabled = seg.Enabled, alert = seg.Enabled && store.GetSetting("segAlert") == "true", max = SegmentationCheck.MaxRules, networks,
            rules = seg.Rules.Select(r => new { id = r.Id, label = r.Label, tag = r.Tag, words = r.Words, type = r.Type, where = r.Where, network = r.Network }),
            findings = seg.Enabled
                ? seg.Evaluate().Select(f => new { ruleId = f.RuleId, rule = f.Rule, hostId = f.HostId, name = f.Name, ip = f.Ip, network = f.Network, expected = f.Expected, where = f.Where, why = f.Why }).ToList()
                : [],
        };
    }

    private static object PoolsJson(HostStore store, PoolWatch pools, NetworkGear gear) => new
    {
        enabled = pools.Enabled, alert = pools.Enabled && store.GetSetting("poolAlert") == "true", percent = pools.Percent, max = PoolWatch.MaxPools,
        pools = pools.Enabled ? pools.Usages().Select(u => new { network = u.Pool.Network, name = u.Pool.Name, start = u.Pool.Start, end = u.Pool.End, source = u.Pool.Source, size = u.Size, used = u.Used, percent = u.Percent }).ToList() : [],
        manual = pools.Manual.Select(p => new { network = p.Network, name = p.Name, start = p.Start, end = p.End }),
        controller = new { configured = gear.UniFiSettings() is not null, error = pools.ControllerError, at = pools.ControllerAt },
    };

    public static void Map(WebApplication app)
    {
        // ---------- the cutover check ----------
        app.MapGet("/api/cutover", (HostStore store) => Results.Json(new { snapshots = store.GetSnapshots().Select(SnapshotJson), max = HostStore.MaxSnapshots }));

        app.MapPost("/api/cutover", (CutoverRequest? body, HostStore store) =>
        {
            var name = (body?.Name ?? "").Trim();
            if (name == "") name = "Before " + DateTime.Now.ToString("d MMM yyyy, HH:mm");
            if (name.Length > 60) name = name[..60];
            var devices = Cutover.Take(store.GetAll());
            if (devices.Count == 0) return Results.BadRequest(new { error = "BAMF hasn't found any devices yet, so there is nothing to keep." });
            var id = store.SaveSnapshot(name, devices);
            return Results.Json(new { id, snapshots = store.GetSnapshots().Select(SnapshotJson), max = HostStore.MaxSnapshots });
        });

        app.MapGet("/api/cutover/{id:long}/compare", (long id, HostStore store) =>
        {
            if (store.GetSnapshot(id) is not { } snap) return Results.NotFound();
            var r = Cutover.Compare(snap.Devices, store.GetAll());
            return Results.Json(new
            {
                snapshot = SnapshotJson(snap.Info), wasOnline = r.WasOnline, wasOffline = r.WasOffline,
                back = r.Back.Select(ItemJson), moved = r.Moved.Select(ItemJson), missing = r.Missing.Select(ItemJson), added = r.New.Select(ItemJson),
                sameName = r.SameName.Select(p => new { was = ItemJson(p.Was), now = ItemJson(p.Now) }),
            });
        });

        app.MapDelete("/api/cutover/{id:long}", (long id, HostStore store) => store.DeleteSnapshot(id) ? Results.Ok() : Results.NotFound());

        // ---------- the segmentation check ----------
        app.MapGet("/api/segmentation", (HostStore store, ScannerService scanner, SegmentationCheck seg) => Results.Json(SegmentationJson(store, scanner, seg)));

        app.MapPost("/api/settings/segmentation", async (SegmentationRequest body, HostStore store, ScannerService scanner, SegmentationCheck seg, CancellationToken ct) =>
        {
            if (body.Rules is { } wanted)
            {
                var (clean, error) = SegmentationCheck.Clean(wanted.Select(r => new SegmentationCheck.Rule(r.Id ?? "", r.Label ?? "", r.Tag ?? "", r.Words ?? new(), r.Type ?? "", r.Where ?? "", r.Network ?? "")));
                if (clean is null) return Results.BadRequest(new { error });
                store.SetJsonList("segRules", clean);
            }
            var alertWas = store.GetSetting("segAlert") == "true";
            if (body.Alert is { } a) store.SetSetting("segAlert", a ? "true" : "false");
            if (body.Enabled is { } on) store.SetSetting("segOn", on ? "true" : "false");
            // Switching the alert on speaks of what happens after, not of what is already so (that is in the list).
            if (body.Alert == true && !alertWas && seg.Enabled) seg.Baseline();
            if (body.Enabled == true && seg.AlertOn && body.Alert is null) seg.Baseline();
            return Results.Json(SegmentationJson(store, scanner, seg));
        });

        // ---------- DHCP pool pressure ----------
        app.MapGet("/api/pools", (HostStore store, PoolWatch pools, NetworkGear gear) => Results.Json(PoolsJson(store, pools, gear)));

        app.MapPost("/api/settings/pools", async (PoolsRequest body, HostStore store, PoolWatch pools, NetworkGear gear, CancellationToken ct) =>
        {
            if (body.Pools is { } wanted)
            {
                var (clean, error) = PoolWatch.Clean(wanted.Select(p => new PoolWatch.Pool(p.Network ?? "", p.Name ?? "", p.Start ?? "", p.End ?? "", "manual")));
                if (clean is null) return Results.BadRequest(new { error });
                store.SetJsonList("poolManual", clean);
            }
            if (body.Percent is { } pct)
            {
                if (pct is < 50 or > 99) return Results.BadRequest(new { error = "From 50 to 99 percent." });
                store.SetSetting("poolPercent", pct.ToString());
            }
            var alertWas = store.GetSetting("poolAlert") == "true";
            if (body.Alert is { } a) store.SetSetting("poolAlert", a ? "true" : "false");
            var wasOn = pools.Enabled;
            if (body.Enabled is { } on) store.SetSetting("poolOn", on ? "true" : "false");
            // Switching the alert (or the watch, with the alert already on) on speaks of what happens next, not of a pool that is already full.
            if (pools.AlertOn && (body.Alert == true && !alertWas || body.Enabled == true && !wasOn)) pools.Baseline();
            if (pools.Enabled && (body.Enabled == true && !wasOn || body.Pools is not null || body.Percent is not null)) await pools.Run(ct);
            return Results.Json(PoolsJson(store, pools, gear));
        });
    }
}

internal sealed record CutoverRequest(string? Name);
internal sealed record SegmentationRuleRequest(string? Id, string? Label, string? Tag, List<string>? Words, string? Type, string? Where, string? Network);
internal sealed record SegmentationRequest(bool? Enabled, bool? Alert, List<SegmentationRuleRequest>? Rules);
internal sealed record PoolRequest(string? Network, string? Name, string? Start, string? End);
internal sealed record PoolsRequest(bool? Enabled, bool? Alert, int? Percent, List<PoolRequest>? Pools);
