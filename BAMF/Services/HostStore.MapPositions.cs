using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// Where the user dragged things on the topology Map, per network card. Kept
/// on the server so the layout is the same in every browser. Only nodes the
/// user moved are stored; everything else is placed by the automatic layout.
/// Node keys: "h:&lt;host id&gt;", "s:&lt;switch id&gt;", "gw", "self", "net", "box".
/// </summary>
public partial class HostStore
{
    public const int MaxMapPositionsPerSave = 500;
    private const double MaxMapCoordinate = 100_000;
    // The whole-network view adds a box per network ("box:192.168.1.0/24") and a
    // top per gateway, keyed by device id or, when no scan has seen it, address
    // ("gw:12", "gw:192.168.1.1").
    private static readonly Regex MapNodeKey = new(
        @"^(h:\d{1,12}|s:\d{1,12}|gw(:[0-9A-Fa-f.:]{1,45})?|self|net|box(:[0-9A-Fa-f.:]{1,45}/\d{1,3})?)$",
        RegexOptions.CultureInvariant);

    private static void InitMapPositions(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS map_positions (
                subnet TEXT NOT NULL,
                node   TEXT NOT NULL,
                x      REAL NOT NULL,
                y      REAL NOT NULL,
                PRIMARY KEY (subnet, node)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>subnet -> node key -> [x, y].</summary>
    public Dictionary<string, Dictionary<string, double[]>> GetMapPositions()
    {
        lock (_lock)
        {
            using var conn = Open();
            var map = new Dictionary<string, Dictionary<string, double[]>>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT subnet, node, x, y FROM map_positions";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!map.TryGetValue(r.GetString(0), out var nodes)) map[r.GetString(0)] = nodes = new();
                nodes[r.GetString(1)] = new[] { Math.Round(r.GetDouble(2), 1), Math.Round(r.GetDouble(3), 1) };
            }
            return map;
        }
    }

    /// <summary>
    /// Saves (or, for a null position, forgets) where nodes sit on one network's
    /// card. Returns an error for the user, or null.
    /// </summary>
    public string? SaveMapPositions(string subnet, IReadOnlyDictionary<string, double[]?> positions)
    {
        subnet = (subnet ?? "").Trim();
        if (subnet.Length == 0 || subnet.Length > 64) return "Which network is this for?";
        if (positions.Count > MaxMapPositionsPerSave) return "Too many positions in one save.";
        foreach (var (key, pos) in positions)
        {
            if (!MapNodeKey.IsMatch(key)) return $"Unknown map node \"{key}\".";
            if (pos is null) continue;
            if (pos.Length != 2 || pos.Any(v => double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > MaxMapCoordinate))
                return "A position is out of range.";
        }
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var (key, pos) in positions)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                if (pos is null)
                {
                    cmd.CommandText = "DELETE FROM map_positions WHERE subnet = $s AND node = $n";
                }
                else
                {
                    cmd.CommandText = """
                        INSERT INTO map_positions (subnet, node, x, y) VALUES ($s, $n, $x, $y)
                        ON CONFLICT(subnet, node) DO UPDATE SET x = $x, y = $y
                        """;
                    cmd.Parameters.AddWithValue("$x", pos[0]);
                    cmd.Parameters.AddWithValue("$y", pos[1]);
                }
                cmd.Parameters.AddWithValue("$s", subnet);
                cmd.Parameters.AddWithValue("$n", key);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return null;
        }
    }

    /// <summary>"Auto-arrange": forgets every position on one network's card.</summary>
    public void ClearMapPositions(string subnet)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM map_positions WHERE subnet = $s";
            cmd.Parameters.AddWithValue("$s", (subnet ?? "").Trim());
            cmd.ExecuteNonQuery();
        }
    }

    private static void ForgetMapNode(SqliteConnection conn, string key, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM map_positions WHERE node = $n";
        cmd.Parameters.AddWithValue("$n", key);
        cmd.ExecuteNonQuery();
    }
}
