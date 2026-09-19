using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// The recorded layout as one JSON file: switches, routers, access points,
/// SSIDs, VPNs and virtual switches, what's plugged in where, port labels,
/// declared gateways, combined network cards, dragged Map positions, and each
/// device's name, note, link, flags, type and tags. Devices are keyed by MAC
/// and switches by their place in the file, so the file means the same thing
/// on another server. Import replaces the layout wholesale; devices the new
/// server hasn't seen yet are skipped and named, so a second import once it
/// has seen them fills the rest in.
/// </summary>
public partial class HostStore
{
    public JsonObject ExportLayout(string version)
    {
        lock (_lock)
        {
            using var conn = Open();
            var hosts = GetAllInternal(conn);
            var macOf = hosts.ToDictionary(h => h.Id, h => h.Mac);
            var switches = GetSwitchesInternal(conn);
            var index = switches.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
            var placements = GetPlacementsInternal(conn);
            var labels = new Dictionary<long, Dictionary<int, string>>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT switch_id, port, label FROM port_labels";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!labels.TryGetValue(r.GetInt64(0), out var l)) labels[r.GetInt64(0)] = l = new();
                    l[r.GetInt32(1)] = r.GetString(2);
                }
            }
            var gateways = GetGatewaysInternal(conn);
            var interfaces = new Dictionary<long, long>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT host_id, parent_id FROM host_interfaces";
                using var r = cmd.ExecuteReader();
                while (r.Read()) interfaces[r.GetInt64(0)] = r.GetInt64(1);
            }
            var types = new Dictionary<long, string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT host_id, type FROM device_types";
                using var r = cmd.ExecuteReader();
                while (r.Read()) types[r.GetInt64(0)] = r.GetString(1);
            }
            var tags = GetTagsInternal(conn);
            var positions = new JsonObject();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT subnet, node, x, y FROM map_positions";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var subnet = Portable(r.GetString(0), macOf, index);
                    var node = Portable(r.GetString(1), macOf, index);
                    if (subnet is null || node is null) continue;
                    if (positions[subnet] is not JsonObject per) positions[subnet] = per = new JsonObject();
                    per[node] = new JsonArray(Math.Round(r.GetDouble(2), 1), Math.Round(r.GetDouble(3), 1));
                }
            }
            string? typeIcons = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT value FROM settings WHERE key = 'typeIcons'";
                typeIcons = cmd.ExecuteScalar() as string;
            }

            var devices = new JsonArray();
            foreach (var h in hosts.Where(h => !h.Forgotten))
            {
                var d = new JsonObject
                {
                    ["mac"] = h.Mac, ["seenAs"] = h.CustomName != "" ? h.CustomName : h.Hostname != "" ? h.Hostname : h.Ip,
                    ["name"] = h.CustomName, ["note"] = h.Note, ["link"] = h.Link,
                    ["known"] = h.Known, ["watched"] = h.Watched, ["ignored"] = h.Ignored,
                };
                if (types.TryGetValue(h.Id, out var t)) d["type"] = t;
                if (tags.TryGetValue(h.Id, out var tg)) d["tags"] = new JsonArray(tg.Select(x => (JsonNode)x).ToArray());
                if (interfaces.TryGetValue(h.Id, out var parent) && macOf.TryGetValue(parent, out var pm)) d["cardOf"] = pm;
                if (placements.TryGetValue(h.Id, out var pl) && index.TryGetValue(pl.Item1, out var si))
                    d["plug"] = new JsonObject { ["switch"] = si, ["port"] = pl.Item2 };
                devices.Add(d);
            }
            var sws = new JsonArray();
            foreach (var s in switches)
            {
                var o = new JsonObject
                {
                    ["name"] = s.Name, ["kind"] = s.Kind, ["ports"] = s.Ports, ["subnet"] = s.Subnet,
                    ["host"] = s.HostId != 0 && macOf.TryGetValue(s.HostId, out var hm) ? hm : null,
                    ["uplink"] = s.Uplink,
                    ["uplinkSwitch"] = s.Uplink == "switch" && index.TryGetValue(s.UplinkSwitch, out var ui) ? ui : null,
                    ["uplinkPort"] = s.UplinkPort,
                    ["runsOn"] = s.Kind == "virtual" ? (s.RunsOn != 0 && macOf.TryGetValue(s.RunsOn, out var rm) ? rm : null)
                               : s.Kind == "ssid" ? (index.TryGetValue(s.RunsOn, out var ri) ? ri : null) : null,
                };
                if (labels.TryGetValue(s.Id, out var l))
                {
                    var lo = new JsonObject();
                    foreach (var (port, text) in l) lo[port.ToString()] = text;
                    o["portLabels"] = lo;
                }
                sws.Add(o);
            }
            var gws = new JsonArray();
            foreach (var g in gateways)
                if (macOf.TryGetValue(g.HostId, out var gm)) gws.Add(new JsonObject { ["subnet"] = g.Subnet, ["mac"] = gm, ["ip"] = g.Ip });

            var root = new JsonObject
            {
                ["bamf"] = version, ["format"] = 1, ["exported"] = DateTime.UtcNow.ToString("o"),
                ["devices"] = devices, ["switches"] = sws, ["gateways"] = gws, ["mapPositions"] = positions,
            };
            if (typeIcons is not null) { try { root["typeIcons"] = JsonNode.Parse(typeIcons); } catch { } }
            return root;
        }
    }

    // "h:12" -> "h:<mac>", "s:3" -> "s:<index>", "gw:12" -> "gw:<mac>"; anything else as is.
    private static string? Portable(string key, Dictionary<long, string> macOf, Dictionary<long, int> index)
    {
        if (key.StartsWith("h:") && long.TryParse(key[2..], out var h)) return macOf.TryGetValue(h, out var m) ? "h:" + m : null;
        if (key.StartsWith("s:") && long.TryParse(key[2..], out var s)) return index.TryGetValue(s, out var i) ? "s:" + i : null;
        if (key.StartsWith("gw:") && long.TryParse(key[3..], out var g)) return macOf.TryGetValue(g, out var gm) ? "gw:" + gm : null;
        return key;
    }
    private static string? Concrete(string key, Dictionary<string, long> idOf, Dictionary<int, long> switchId)
    {
        if (key.StartsWith("h:")) return idOf.TryGetValue(key[2..], out var h) ? "h:" + h : null;
        if (key.StartsWith("s:") && int.TryParse(key[2..], out var s)) return switchId.TryGetValue(s, out var id) ? "s:" + id : null;
        if (key.StartsWith("gw:") && key.Length > 3 && key[3..].Contains(':')) return idOf.TryGetValue(key[3..], out var g) ? "gw:" + g : null;
        return key;
    }

    public sealed record ImportResult(int Devices, int Switches, List<string> Skipped, string? Error);

    // A number, or nothing: a null or a string in the file is "not given", not an error.
    private static bool Int(JsonElement e, out int v)
    {
        v = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out v);
    }

    /// <summary>Replaces the recorded layout with the file's. Devices the file names that this server hasn't seen are skipped.</summary>
    public ImportResult ImportLayout(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("switches", out var swEl) || swEl.ValueKind != JsonValueKind.Array)
            return new(0, 0, new(), "That isn't a BAMF layout file.");
        var skipped = new List<string>();
        lock (_lock)
        {
            using var conn = Open();
            var idOf = GetAllInternal(conn).ToDictionary(h => h.Mac, h => h.Id, StringComparer.OrdinalIgnoreCase);
            string? Mac(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long? HostFor(string? mac)
            {
                if (string.IsNullOrEmpty(mac)) return null;
                if (idOf.TryGetValue(mac, out var id)) return id;
                if (!skipped.Contains(mac, StringComparer.OrdinalIgnoreCase)) skipped.Add(mac);
                return null;
            }
            using var tx = conn.BeginTransaction();
            void Exec(string sql, params (string, object)[] ps)
            {
                using var c = conn.CreateCommand();
                c.Transaction = tx; c.CommandText = sql;
                foreach (var (k, v) in ps) c.Parameters.AddWithValue(k, v);
                c.ExecuteNonQuery();
            }
            Exec("DELETE FROM placements; DELETE FROM port_labels; DELETE FROM switches; DELETE FROM gateways; DELETE FROM host_interfaces; DELETE FROM map_positions; DELETE FROM device_types; DELETE FROM host_tags;");

            // Switches first, then the references between them.
            var switchId = new Dictionary<int, long>();
            var list = swEl.EnumerateArray().ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var s = list[i];
                var kind = (Mac(s, "kind") ?? "switch").ToLowerInvariant();
                if (!SwitchKinds.Contains(kind)) kind = "switch";
                var name = (Mac(s, "name") ?? "").Trim();
                if (name.Length == 0) name = $"Switch {i + 1}";
                if (name.Length > 60) name = name[..60];
                var ports = s.TryGetProperty("ports", out var pe) && Int(pe, out var pn) ? Math.Clamp(pn, 1, MaxSwitchPorts) : 1;
                var uplink = (Mac(s, "uplink") ?? "").ToLowerInvariant();
                if (uplink is not ("router" or "switch")) uplink = "";
                var uplinkPort = s.TryGetProperty("uplinkPort", out var upe) && Int(upe, out var up) ? Math.Max(0, up) : 0;
                var hostId = HostFor(Mac(s, "host")) ?? 0;
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO switches (name, ports, subnet, host_id, uplink, uplink_switch, uplink_port, kind, runs_on)
                    VALUES ($n, $p, $s, $h, $u, 0, $up, $k, 0); SELECT last_insert_rowid();
                    """;
                ins.Parameters.AddWithValue("$n", name); ins.Parameters.AddWithValue("$p", ports);
                ins.Parameters.AddWithValue("$s", Mac(s, "subnet") ?? ""); ins.Parameters.AddWithValue("$h", hostId);
                ins.Parameters.AddWithValue("$u", uplink); ins.Parameters.AddWithValue("$up", uplinkPort); ins.Parameters.AddWithValue("$k", kind);
                switchId[i] = Convert.ToInt64(ins.ExecuteScalar());
            }
            for (var i = 0; i < list.Count; i++)
            {
                var s = list[i];
                var kind = (Mac(s, "kind") ?? "switch").ToLowerInvariant();
                long uplinkSwitch = 0, runsOn = 0;
                if (s.TryGetProperty("uplinkSwitch", out var use) && Int(use, out var ui) && switchId.TryGetValue(ui, out var uid) && uid != switchId[i]) uplinkSwitch = uid;
                if (s.TryGetProperty("runsOn", out var ro))
                {
                    if (kind == "ssid" && Int(ro, out var ri) && switchId.TryGetValue(ri, out var rid)) runsOn = rid;
                    else if (kind == "virtual" && ro.ValueKind == JsonValueKind.String) runsOn = HostFor(ro.GetString()) ?? 0;
                }
                Exec("UPDATE switches SET uplink_switch = $us, runs_on = $ro, uplink = CASE WHEN uplink = 'switch' AND $us = 0 THEN '' ELSE uplink END WHERE id = $id",
                    ("$us", uplinkSwitch), ("$ro", runsOn), ("$id", switchId[i]));
                if (s.TryGetProperty("portLabels", out var pl) && pl.ValueKind == JsonValueKind.Object)
                    foreach (var prop in pl.EnumerateObject())
                        if (int.TryParse(prop.Name, out var port) && port >= 1 && prop.Value.ValueKind == JsonValueKind.String)
                        {
                            var text = (prop.Value.GetString() ?? "").Trim();
                            if (text.Length > MaxPortLabel) text = text[..MaxPortLabel];
                            if (text.Length > 0) Exec("INSERT OR REPLACE INTO port_labels (switch_id, port, label) VALUES ($s, $p, $l)", ("$s", switchId[i]), ("$p", port), ("$l", text));
                        }
            }

            var devices = 0;
            if (root.TryGetProperty("devices", out var devEl) && devEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in devEl.EnumerateArray())
                {
                    var id = HostFor(Mac(d, "mac"));
                    if (id is null) continue;
                    devices++;
                    string S(string n) { var v = Mac(d, n) ?? ""; return v.Length > 500 ? v[..500] : v; }
                    bool B(string n) => d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
                    Exec("UPDATE hosts SET custom_name = $n, note = $note, link = $l, known = $k, watched = $w, ignored = $i WHERE id = $id",
                        ("$n", S("name").Length > 60 ? S("name")[..60] : S("name")), ("$note", S("note")), ("$l", S("link")),
                        ("$k", B("known") ? 1 : 0), ("$w", B("watched") ? 1 : 0), ("$i", B("ignored") ? 1 : 0), ("$id", id.Value));
                    var type = Mac(d, "type");
                    if (type is not null && DeviceTypeKeys.Contains(type)) Exec("INSERT OR REPLACE INTO device_types (host_id, type) VALUES ($h, $t)", ("$h", id.Value), ("$t", type));
                    if (d.TryGetProperty("tags", out var te) && te.ValueKind == JsonValueKind.Array)
                        foreach (var t in te.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => (x.GetString() ?? "").Trim()).Where(x => x.Length is > 0 and <= MaxTagLength).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxTagsPerHost))
                            Exec("INSERT OR IGNORE INTO host_tags (host_id, tag) VALUES ($h, $t)", ("$h", id.Value), ("$t", t));
                    if (d.TryGetProperty("plug", out var pe) && pe.ValueKind == JsonValueKind.Object
                        && pe.TryGetProperty("switch", out var ps) && Int(ps, out var si) && switchId.TryGetValue(si, out var sid))
                    {
                        var port = pe.TryGetProperty("port", out var pp) && Int(pp, out var pn) ? Math.Max(0, pn) : 0;
                        Exec("INSERT OR REPLACE INTO placements (host_id, switch_id, port) VALUES ($h, $s, $p)", ("$h", id.Value), ("$s", sid), ("$p", port));
                    }
                }
                // Combined cards, once every device is known.
                foreach (var d in devEl.EnumerateArray())
                {
                    var id = HostFor(Mac(d, "mac"));
                    var parent = HostFor(Mac(d, "cardOf"));
                    if (id is null || parent is null || id == parent) continue;
                    Exec("INSERT OR REPLACE INTO host_interfaces (host_id, parent_id) VALUES ($h, $p)", ("$h", id.Value), ("$p", parent.Value));
                }
            }
            if (root.TryGetProperty("gateways", out var gwEl) && gwEl.ValueKind == JsonValueKind.Array)
                foreach (var g in gwEl.EnumerateArray())
                {
                    var id = HostFor(Mac(g, "mac"));
                    var subnet = Mac(g, "subnet"); var ip = Mac(g, "ip");
                    if (id is null || string.IsNullOrEmpty(subnet) || string.IsNullOrEmpty(ip)) continue;
                    Exec("INSERT OR REPLACE INTO gateways (subnet, host_id, ip) VALUES ($s, $h, $ip)", ("$s", subnet), ("$h", id.Value), ("$ip", ip));
                }
            if (root.TryGetProperty("mapPositions", out var mpEl) && mpEl.ValueKind == JsonValueKind.Object)
                foreach (var per in mpEl.EnumerateObject())
                {
                    var subnet = Concrete(per.Name, idOf, switchId);
                    if (subnet is null || per.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var node in per.Value.EnumerateObject())
                    {
                        var key = Concrete(node.Name, idOf, switchId);
                        if (key is null || !MapNodeKey.IsMatch(key) || node.Value.ValueKind != JsonValueKind.Array || node.Value.GetArrayLength() != 2) continue;
                        var xy = node.Value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var f) ? f : double.NaN).ToArray();
                        if (xy.Any(double.IsNaN) || xy.Any(v => Math.Abs(v) > MaxMapCoordinate)) continue;
                        Exec("INSERT OR REPLACE INTO map_positions (subnet, node, x, y) VALUES ($s, $n, $x, $y)", ("$s", subnet), ("$n", key), ("$x", xy[0]), ("$y", xy[1]));
                    }
                }
            if (root.TryGetProperty("typeIcons", out var ti) && ti.ValueKind == JsonValueKind.Object)
                Exec("INSERT OR REPLACE INTO settings (key, value) VALUES ('typeIcons', $v)", ("$v", ti.GetRawText()));
            tx.Commit();
            return new(devices, list.Count, skipped, null);
        }
    }
}
