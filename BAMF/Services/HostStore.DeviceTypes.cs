using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// Device types the user set, overriding BAMF's guess. A type is one of a
/// fixed list, each with an icon on the Map, and it's also what the device
/// list's type chips group by. Two levels: a type for one device, and an icon
/// for every device of a guessed type ("every Linux device is a server").
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

    private static void InitDeviceTypes(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS device_types (
                host_id INTEGER PRIMARY KEY,
                type    TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
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

    /// <summary>Sets a device's type; empty goes back to BAMF's guess. Returns an error, or null.</summary>
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
        cmd.CommandText = "DELETE FROM device_types WHERE host_id = $id";
        cmd.Parameters.AddWithValue("$id", hostId);
        cmd.ExecuteNonQuery();
    }
}
