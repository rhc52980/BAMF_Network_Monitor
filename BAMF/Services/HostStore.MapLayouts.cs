using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// Named layouts for a Map: where everything you placed on one network's card sat when you saved it, kept under a name so you can switch
/// between arrangements of the same map and come back. A layout is only a set of positions (the same keys as <c>map_positions</c>);
/// applying one is the browser saving those positions as an ordinary, undoable move, so nothing here touches the live map.
/// </summary>
public partial class HostStore
{
    public const int MaxMapLayoutsPerMap = 30, MaxMapLayoutName = 40, MaxMapLayoutNodes = 1500;

    private static void InitMapLayouts(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS map_layouts (
                subnet  TEXT NOT NULL,
                name    TEXT NOT NULL COLLATE NOCASE,
                data    TEXT NOT NULL,
                created TEXT NOT NULL,
                PRIMARY KEY (subnet, name)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public record MapLayout(string Name, string Created, Dictionary<string, double[]> Positions);

    /// <summary>The layouts saved for one map, oldest first.</summary>
    public List<MapLayout> GetMapLayouts(string subnet)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name, data, created FROM map_layouts WHERE subnet = $s ORDER BY created, name";
            cmd.Parameters.AddWithValue("$s", (subnet ?? "").Trim());
            var list = new List<MapLayout>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                try
                {
                    var pos = JsonSerializer.Deserialize<Dictionary<string, double[]>>(r.GetString(1));
                    if (pos is not null) list.Add(new(r.GetString(0), r.GetString(2), pos));
                }
                catch (JsonException) { }
            }
            return list;
        }
    }

    /// <summary>Saves a layout under a name, replacing one of the same name (capital letters aside). Returns an error for the user, or null.</summary>
    public string? SaveMapLayout(string subnet, string? name, IReadOnlyDictionary<string, double[]?> positions)
    {
        subnet = (subnet ?? "").Trim();
        if (subnet.Length == 0 || subnet.Length > 64) return "Which network is this for?";
        name = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) return "Give the layout a name.";
        if (name.Length > MaxMapLayoutName) return $"Keep the name to {MaxMapLayoutName} characters.";
        if (name.Any(char.IsControl)) return "That name has a character that can't be kept.";
        if (positions.Count > MaxMapLayoutNodes) return "Too many things in one layout.";
        var clean = new Dictionary<string, double[]>();
        foreach (var (key, pos) in positions)
        {
            if (!MapNodeKey.IsMatch(key)) return $"Unknown map node \"{key}\".";
            if (pos is null) continue;
            if (pos.Length != 2 || pos.Any(v => double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > MaxMapCoordinate)) return "A position is out of range.";
            clean[key] = new[] { Math.Round(pos[0], 1), Math.Round(pos[1], 1) };
        }
        if (clean.Count == 0) return "There is nothing placed on this map to save: move something first.";
        lock (_lock)
        {
            using var conn = Open();
            using (var count = conn.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM map_layouts WHERE subnet = $s AND name <> $n";
                count.Parameters.AddWithValue("$s", subnet); count.Parameters.AddWithValue("$n", name);
                if (Convert.ToInt32(count.ExecuteScalar()) >= MaxMapLayoutsPerMap) return $"That is as many layouts as one map keeps ({MaxMapLayoutsPerMap}). Delete one first.";
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO map_layouts (subnet, name, data, created) VALUES ($s, $n, $d, $c)
                ON CONFLICT(subnet, name) DO UPDATE SET data = $d, created = $c, name = $n
                """;
            cmd.Parameters.AddWithValue("$s", subnet); cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$d", JsonSerializer.Serialize(clean));
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            return null;
        }
    }

    /// <summary>Deletes a layout. False when there was none by that name.</summary>
    public bool DeleteMapLayout(string subnet, string? name)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM map_layouts WHERE subnet = $s AND name = $n";
            cmd.Parameters.AddWithValue("$s", (subnet ?? "").Trim()); cmd.Parameters.AddWithValue("$n", (name ?? "").Trim());
            return cmd.ExecuteNonQuery() > 0;
        }
    }
}
