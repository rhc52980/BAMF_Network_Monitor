using LanWatch.Services;
using static LanWatch.Api.ApiHelpers;

namespace LanWatch.Api;

/// <summary>The network's layout: switches and their ports, gateways, Find port, the map.</summary>
internal static class LayoutEndpoints
{
    public static void Map(WebApplication app)
    {
        // The user's own account of how things are cabled, for the map. BAMF can't
        // discover it: ARP shows presence, and budget smart switches don't expose
        // their MAC table. Nothing here talks to a switch.

        app.MapPost("/api/switches", (SwitchInput body, HostStore store) =>
        {
            var (saved, error) = store.SaveSwitch(null, body);
            return saved is null ? Results.BadRequest(new { error }) : Results.Json(SwitchJson(saved, store.GetPortLabels()));
        });

        app.MapPost("/api/switches/{id:long}", (long id, SwitchInput body, HostStore store) =>
        {
            var (saved, error) = store.SaveSwitch(id, body);
            return saved is null ? Results.BadRequest(new { error }) : Results.Json(SwitchJson(saved, store.GetPortLabels()));
        });

        app.MapDelete("/api/switches/{id:long}", (long id, HostStore store) =>
        {
            if (!store.DeleteSwitch(id)) return Results.NotFound();
            store.DeleteSnmpConfig(id);
            return Results.Ok();
        });

        // Traffic counters from managed switches over SNMP: each switch's settings
        // and what it last said. The community is never sent back, only whether one's saved.
        app.MapGet("/api/switches/snmp", (SwitchCounters counters) => Results.Json(counters.Statuses()));

        app.MapPost("/api/switches/{id:long}/snmp", async (long id, SnmpRequest body, HostStore store, SwitchCounters counters, CancellationToken ct) =>
        {
            var sw = store.GetSwitches().FirstOrDefault(s => s.Id == id);
            if (sw is null) return Results.NotFound();
            if (sw.Kind is not ("switch" or "router")) return Results.BadRequest(new { error = "Only a switch or router has ports to read." });
            var address = (body.Address ?? "").Trim();
            if (address != "" && !System.Net.IPEndPoint.TryParse(address, out _)) return Results.BadRequest(new { error = "That isn't an IP address (add :port for one that isn't 161)." });
            var community = body.Community is null ? null : body.Community.Trim();
            if (community is { Length: > 64 }) return Results.BadRequest(new { error = "That community is too long." });
            store.SaveSnmpConfig(id, body.Enabled, address, community == "" ? null : community);
            // Read it straight away, so the dialog can say whether it worked.
            var status = body.Enabled ? await counters.PollOne(id, ct) : counters.Statuses().FirstOrDefault(s => s.SwitchId == id);
            return Results.Json(status);
        });

        // Everything on one switch at once, from its Ports dialog: the hosts listed
        // are placed on it (moving off any other switch), the rest come off it.
        app.MapPost("/api/switches/{id:long}/ports", (long id, SwitchPortsRequest body, HostStore store) =>
        {
            var entries = (body.Ports ?? new()).Select(p => (p.HostId, p.Port)).ToList();
            var labels = body.Labels?.Select(l => (l.Port, l.Label ?? "")).ToList();
            var error = store.SetSwitchPorts(id, entries, labels);
            return error is null ? Results.Ok() : Results.BadRequest(new { error });
        });

        // "Find port": pulse a device's switch-port light so the user can see which
        // port it's on. On demand, one device at a time, known private hosts only -
        // the same boundary as the port checks, since the dashboard may have no password.
        app.MapPost("/api/hosts/{id:long}/blink", (long id, BlinkRequest? body, HostStore store, PortBlinker blinker) =>
        {
            var host = store.GetAll().FirstOrDefault(h => h.Id == id);
            if (host is null) return Results.NotFound();
            if (!System.Net.IPAddress.TryParse(host.Ip, out var ip) || !IsPrivateAddress(ip))
                return Results.BadRequest(new { error = "Only devices on a private address can be blinked." });
            var (started, until) = blinker.Start(id, ip, body?.Seconds ?? PortBlinker.DefaultSeconds);
            return Results.Json(new
            {
                ok = true, hostId = id, ip = host.Ip,
                started = started.ToString("o"), until = until.ToString("o"), serverTime = DateTime.UtcNow.ToString("o"),
            });
        });

        app.MapDelete("/api/blink", (PortBlinker blinker) =>
        {
            blinker.Stop();
            return Results.Ok();
        });

        // Topology Map layout: where the user dragged things, per network card. A null
        // position forgets that node, so it goes back to the automatic layout.
        app.MapPost("/api/map/positions", (MapPositionsRequest body, HostStore store) =>
        {
            var error = store.SaveMapPositions(body.Subnet ?? "", body.Positions ?? new());
            return error is null ? Results.Ok() : Results.BadRequest(new { error });
        });

        // "Auto-arrange": forget every dragged position on one network's card.
        app.MapDelete("/api/map/positions", (string? subnet, HostStore store) =>
        {
            if (string.IsNullOrWhiteSpace(subnet)) return Results.BadRequest(new { error = "Which network is this for?" });
            store.ClearMapPositions(subnet);
            return Results.Ok();
        });

        // Icons for whole guessed types: { "icons": { "Linux": "server", "Printer": "" } }.
        // An empty icon goes back to the automatic one.
        app.MapPost("/api/settings/type-icons", (TypeIconsRequest body, HostStore store) =>
        {
            var error = store.SetTypeIcons(body.Icons ?? new());
            return error is null ? Results.Json(store.GetTypeIcons()) : Results.BadRequest(new { error });
        });

        // Declares a device the gateway of the network one of its addresses is on,
        // or (enabled false) stops declaring it. One gateway per network.
        app.MapPost("/api/hosts/{id:long}/gateway", (long id, GatewayRequest body, HostStore store) =>
        {
            var error = store.SetGateway(id, body.Ip, body.Enabled);
            if (error is not null) return Results.BadRequest(new { error });
            PortChecker.SetDeclaredGateways(store.GetGateways().Select(g => g.Ip));
            return Results.Ok();
        });

        // Combines a device into another as one of its network cards; parentId 0
        // separates it again.
        app.MapPost("/api/hosts/{id:long}/combine", (long id, CombineRequest body, HostStore store) =>
        {
            var error = store.SetInterfaceOf(id, body.ParentId);
            return error is null ? Results.Ok() : Results.BadRequest(new { error });
        });

        // Switch 0 clears the placement; port 0 means "on this switch, port not recorded".
        app.MapPost("/api/hosts/{id:long}/plug", (long id, PlugRequest body, HostStore store) =>
        {
            var error = store.SetPlacement(id, body.SwitchId, body.Port);
            return error is null ? Results.Ok() : Results.BadRequest(new { error });
        });
    }
}
