using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// What a device is, in two separate settings:
///
/// - Its <b>device type</b>: your own words for it ("NAS", "Kids' tablet"),
///   shown in the device list in place of BAMF's guess, grouped by the Device
///   type chips and found by search. Free text, kept in device_kinds.
/// - Its <b>Map icon</b>: one of a fixed list of pictures, used only on the
///   Map. Kept in device_types (the name it had when one setting did both).
///   Two levels: an icon for one device, and an icon for every device of a
///   guessed type ("every Linux device gets the server icon").
///
/// Until the two were separated the icon was the type as well. The first start after
/// that copies each icon's name into the device type, so nothing changes on screen.
/// </summary>
public partial class HostStore
{
    /// <summary>The types a user can pick. Each has an icon in the dashboard.</summary>
    public static readonly string[] DeviceTypeKeys =
    {
        "router", "switch", "ap", "camera", "printer", "tv", "speaker", "phone", "tablet", "laptop",
        "desktop", "server", "nas", "vm", "game", "iot", "light", "plug", "device",
    };

    private const string TypeIconsSetting = "typeIcons";

    /// <summary>The longest a device type can be.</summary>
    public const int MaxTypeName = 40;

    /// <summary>Each icon's name, as the dashboard shows it; used once, to carry icons over as device types.</summary>
    private static readonly Dictionary<string, string> IconNames = new()
    {
        ["router"] = "Router", ["switch"] = "Switch", ["ap"] = "Access point", ["camera"] = "Camera", ["printer"] = "Printer",
        ["tv"] = "TV / media", ["speaker"] = "Speaker", ["phone"] = "Phone", ["tablet"] = "Tablet", ["laptop"] = "Laptop",
        ["desktop"] = "Desktop", ["server"] = "Server", ["nas"] = "NAS", ["vm"] = "Virtual machine", ["game"] = "Game console",
        ["iot"] = "Smart home", ["light"] = "Light", ["plug"] = "Smart plug", ["device"] = "Other",
    };

    private static void InitDeviceTypes(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS device_types (
                host_id INTEGER PRIMARY KEY,
                type    TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS device_kinds (
                host_id INTEGER PRIMARY KEY,
                name    TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // Once: an icon picked before the two were separate was the device's
        // type too, so it becomes the device type as well.
        using var done = conn.CreateCommand();
        done.CommandText = "SELECT COUNT(*) FROM settings WHERE key = 'typeNamesSplit'";
        if (Convert.ToInt64(done.ExecuteScalar()) > 0) return;
        var icons = new List<(long, string)>();
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT host_id, type FROM device_types";
            using var r = read.ExecuteReader();
            while (r.Read()) icons.Add((r.GetInt64(0), r.GetString(1)));
        }
        foreach (var (id, icon) in icons)
        {
            if (!IconNames.TryGetValue(icon, out var name)) continue;
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT OR IGNORE INTO device_kinds (host_id, name) VALUES ($h, $n)";
            ins.Parameters.AddWithValue("$h", id);
            ins.Parameters.AddWithValue("$n", name);
            ins.ExecuteNonQuery();
        }
        using var mark = conn.CreateCommand();
        mark.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ('typeNamesSplit', 'true')";
        mark.ExecuteNonQuery();
    }

    /// <summary>host id -> the device type the user wrote.</summary>
    public Dictionary<long, string> GetTypeNames()
    {
        lock (_lock)
        {
            using var conn = Open();
            var map = new Dictionary<long, string>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT host_id, name FROM device_kinds";
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetInt64(0)] = r.GetString(1);
            return map;
        }
    }

    /// <summary>Sets a device's type in the user's words; empty goes back to BAMF's guess. Returns an error, or null.</summary>
    public string? SetTypeName(long hostId, string? name)
    {
        name = string.Join(" ", (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > MaxTypeName) return $"Keep it to {MaxTypeName} characters.";
        lock (_lock)
        {
            using var conn = Open();
            if (GetByIdInternal(conn, hostId) is null) return "That device no longer exists.";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = name.Length == 0
                ? "DELETE FROM device_kinds WHERE host_id = $h"
                : """
                  INSERT INTO device_kinds (host_id, name) VALUES ($h, $n)
                  ON CONFLICT(host_id) DO UPDATE SET name = $n
                  """;
            cmd.Parameters.AddWithValue("$h", hostId);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.ExecuteNonQuery();
            return null;
        }
    }

    /// <summary>host id -> the type the user set.</summary>
    public Dictionary<long, string> GetDeviceTypes()
    {
        lock (_lock)
        {
            using var conn = Open();
            var map = new Dictionary<long, string>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT host_id, type FROM device_types";
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetInt64(0)] = r.GetString(1);
            return map;
        }
    }

    /// <summary>Sets a device's Map icon; empty goes back to the automatic one. Returns an error, or null.</summary>
    public string? SetDeviceType(long hostId, string? type)
    {
        type = (type ?? "").Trim().ToLowerInvariant();
        if (type.Length > 0 && !DeviceTypeKeys.Contains(type)) return $"Unknown device type \"{type}\".";
        lock (_lock)
        {
            using var conn = Open();
            if (GetByIdInternal(conn, hostId) is null) return "That device no longer exists.";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = type.Length == 0
                ? "DELETE FROM device_types WHERE host_id = $h"
                : """
                  INSERT INTO device_types (host_id, type) VALUES ($h, $t)
                  ON CONFLICT(host_id) DO UPDATE SET type = $t
                  """;
            cmd.Parameters.AddWithValue("$h", hostId);
            cmd.Parameters.AddWithValue("$t", type);
            cmd.ExecuteNonQuery();
            return null;
        }
    }

    /// <summary>Guessed type (e.g. "Linux") -> the icon the user wants for it.</summary>
    public Dictionary<string, string> GetTypeIcons()
    {
        var raw = GetSetting(TypeIconsSetting);
        if (string.IsNullOrEmpty(raw)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(raw) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>
    /// Sets (or, with an empty icon, clears) the icon for guessed types.
    /// Returns an error, or null.
    /// </summary>
    public string? SetTypeIcons(IReadOnlyDictionary<string, string?> changes)
    {
        var icons = GetTypeIcons();
        foreach (var (family, icon) in changes)
        {
            var name = (family ?? "").Trim();
            if (name.Length == 0 || name.Length > 80) return "Which device type is this for?";
            var key = (icon ?? "").Trim().ToLowerInvariant();
            if (key.Length == 0) { icons.Remove(name); continue; }
            if (!DeviceTypeKeys.Contains(key)) return $"Unknown icon \"{key}\".";
            icons[name] = key;
        }
        if (icons.Count > 200) return "That's more device types than BAMF keeps icons for.";
        SetSetting(TypeIconsSetting, JsonSerializer.Serialize(icons));
        return null;
    }

    private static void ForgetDeviceType(SqliteConnection conn, long hostId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM device_types WHERE host_id = $id; DELETE FROM device_kinds WHERE host_id = $id";
        cmd.Parameters.AddWithValue("$id", hostId);
        cmd.ExecuteNonQuery();
    }
}
