using System.Text.Json;
using LanWatch.Services;

namespace LanWatch.Api;

/// <summary>Changing a network's range in place, and taking a network away with a say in what happens to its devices.</summary>
internal static class NetworkEditEndpoints
{
    // What the lists hold, read back from the settings the scanner reads them from.
    private static Dictionary<string, int> SavedIntervals(HostStore store)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, int>>(store.GetSetting("subnetScanIntervalSeconds") ?? "") ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static List<string> SavedPaused(HostStore store)
    {
        try { return JsonSerializer.Deserialize<List<string>>(store.GetSetting("disabledSubnets") ?? "") ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>A network's own pause and interval follow it to a new name, or go when it goes.</summary>
    private static void CarrySettings(HostStore store, string from, string? to)
    {
        var intervals = SavedIntervals(store);
        if (intervals.Remove(from, out var secs))
        {
            if (to is not null) intervals[to] = secs;
            store.SetSetting("subnetScanIntervalSeconds", JsonSerializer.Serialize(intervals));
        }
        var paused = SavedPaused(store);
        if (paused.RemoveAll(p => string.Equals(p, from, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            if (to is not null) paused.Add(to);
            store.SetSetting("disabledSubnets", JsonSerializer.Serialize(paused));
        }
    }

    public static void Map(WebApplication app)
    {
        // What is filed under a network, and, given where it would go, how many would follow.
        app.MapGet("/api/settings/networks/usage", (string? network, string? to, HostStore store, ScannerService scanner) =>
        {
            var net = (network ?? "").Trim();
            if (net == "") return Results.BadRequest(new { error = "Which network?" });
            var devices = store.DevicesOn(net);
            var live = devices.Where(d => !d.Forgotten).ToList();
            var current = scanner.CurrentNetworks();
            string? target = null;
            if (!string.IsNullOrWhiteSpace(to))
            {
                if (ScannerService.CheckNetwork(to, out var label) is { } problem) return Results.BadRequest(new { error = problem });
                target = label;
            }
            return Results.Json(new
            {
                network = net,
                devices = live.Count,
                online = live.Count(d => d.Online),
                forgotten = devices.Count - live.Count,
                // Into the new range: how many of them it holds. Those it doesn't stay where they are.
                fitsNew = target is null ? (int?)null : live.Count(d => ScannerService.NetworkContains(target, d.Ip)),
                // The other networks being scanned that hold some of them.
                moveTo = current.Where(c => c != net)
                    .Select(c => new { network = c, count = live.Count(d => ScannerService.NetworkContains(c, d.Ip)) })
                    .Where(x => x.count > 0).ToList(),
            });
        });

        // Changes a network's range in place: the pause, the interval and the devices follow.
        app.MapPost("/api/settings/networks/edit", (NetworkEditRequest body, HostStore store, ScannerService scanner) =>
        {
            var current = scanner.CurrentNetworks();
            var from = (body.From ?? "").Trim();
            if (!current.Contains(from)) return Results.BadRequest(new { error = $"{from} isn't one of the networks BAMF scans." });
            if (ScannerService.CheckNetwork(body.To, out var to) is { } problem) return Results.BadRequest(new { error = problem });
            if (to == from) return Results.BadRequest(new { error = "That's the same network. Type the one it should become." });
            if (current.Contains(to)) return Results.BadRequest(new { error = $"{to} is already on the list." });

            var list = current.Select(c => c == from ? to : c).ToList();
            store.SetSetting("subnets", JsonSerializer.Serialize(list));
            CarrySettings(store, from, to);
            var moved = store.MoveNetwork(from, to, ip => ScannerService.NetworkContains(to, ip));
            // The ones the new range doesn't hold are left under the old name, unless told to forget them.
            var forgotten = body.ForgetLeft == true ? store.ForgetNetwork(from) : 0;
            var left = store.DevicesOn(from).Count(d => !d.Forgotten);
            scanner.RequestScan(null);
            app.Logger.LogInformation("Network changed in Settings: {From} to {To}, {Moved} devices moved", from, to, moved);
            return Results.Json(new { networks = ApiHelpers.NetworksJson(scanner), network = to, moved, forgotten, left });
        });

        // Takes a network off the list, and does what was asked with its devices: keep them as they are, move the ones another network holds, or forget them.
        app.MapPost("/api/settings/networks/remove", (NetworkRemoveRequest body, HostStore store, ScannerService scanner) =>
        {
            var current = scanner.CurrentNetworks();
            var net = (body.Network ?? "").Trim();
            if (!current.Contains(net)) return Results.BadRequest(new { error = $"{net} isn't one of the networks BAMF scans." });
            if (current.Count < 2) return Results.BadRequest(new { error = "BAMF needs at least one network to scan." });
            var action = (body.Devices ?? "keep").Trim().ToLowerInvariant();
            if (action is not ("keep" or "move" or "forget")) return Results.BadRequest(new { error = "Keep the devices, move them, or forget them." });
            var target = (body.MoveTo ?? "").Trim();
            if (action == "move" && (target == net || !current.Contains(target)))
                return Results.BadRequest(new { error = "Pick one of the networks that stay to move them to." });

            store.SetSetting("subnets", JsonSerializer.Serialize(current.Where(c => c != net).ToList()));
            CarrySettings(store, net, null);
            int moved = 0, forgotten = 0;
            if (action == "move") moved = store.MoveNetwork(net, target, ip => ScannerService.NetworkContains(target, ip));
            // What a move couldn't place is forgotten only when asked; otherwise it stays, as it always has.
            if (action == "forget" || (action == "move" && body.ForgetLeft == true)) forgotten = store.ForgetNetwork(net);
            var left = store.DevicesOn(net).Count(d => !d.Forgotten);
            scanner.RequestScan(null);
            app.Logger.LogInformation("Network removed in Settings: {Network} ({Action}: {Moved} moved, {Forgotten} forgotten)", net, action, moved, forgotten);
            return Results.Json(new { networks = ApiHelpers.NetworksJson(scanner), moved, forgotten, left });
        });
    }
}

internal sealed record NetworkEditRequest(string? From, string? To, bool? ForgetLeft);
internal sealed record NetworkRemoveRequest(string? Network, string? Devices, string? MoveTo, bool? ForgetLeft);
