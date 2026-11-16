using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// Snapshots of the network taken before a change (a new router, a new set of VLANs), to compare against afterwards. A snapshot is
/// every device BAMF knew then, with its address and network; BAMF keeps the five newest. Taken by you, on a button, so there is
/// nothing to switch on.
/// </summary>
public partial class HostStore
{
    public sealed record SnapDevice(string Mac, string Ip, string Name, string Network, bool Online, string Vendor);
    public sealed record SnapshotInfo(long Id, string Name, string At, int Devices, int Online);

    public const int MaxSnapshots = 5;

    private static void InitChecks(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS cutover_snapshots (
                id   INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                at   TEXT NOT NULL,
                json TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Keeps a snapshot, and drops the oldest beyond the newest <see cref="MaxSnapshots"/>. Returns its id.</summary>
    public long SaveSnapshot(string name, IReadOnlyList<SnapDevice> devices)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            long id;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO cutover_snapshots (name, at, json) VALUES ($n, $a, $j); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$a", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(devices));
                id = Convert.ToInt64(cmd.ExecuteScalar());
            }
            using (var trim = conn.CreateCommand())
            {
                trim.Transaction = tx;
                trim.CommandText = "DELETE FROM cutover_snapshots WHERE id NOT IN (SELECT id FROM cutover_snapshots ORDER BY id DESC LIMIT $m)";
                trim.Parameters.AddWithValue("$m", MaxSnapshots);
                trim.ExecuteNonQuery();
            }
            tx.Commit();
            return id;
        }
    }

    public List<SnapshotInfo> GetSnapshots()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, name, at, json FROM cutover_snapshots ORDER BY id DESC";
            var list = new List<SnapshotInfo>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var devices = Devices(r.GetString(3));
                list.Add(new SnapshotInfo(r.GetInt64(0), r.GetString(1), r.GetString(2), devices.Count, devices.Count(d => d.Online)));
            }
            return list;
        }
    }

    public (SnapshotInfo Info, List<SnapDevice> Devices)? GetSnapshot(long id)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, name, at, json FROM cutover_snapshots WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var devices = Devices(r.GetString(3));
            return (new SnapshotInfo(r.GetInt64(0), r.GetString(1), r.GetString(2), devices.Count, devices.Count(d => d.Online)), devices);
        }
    }

    public bool DeleteSnapshot(long id)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM cutover_snapshots WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    private static List<SnapDevice> Devices(string json)
    {
        try { return JsonSerializer.Deserialize<List<SnapDevice>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>A list kept as JSON in a setting: the segmentation rules, the pools you entered, what has already been alerted.</summary>
    public List<T> GetJsonList<T>(string key)
    {
        try { return JsonSerializer.Deserialize<List<T>>(GetSetting(key) ?? "[]") ?? new(); }
        catch (JsonException) { return new(); }
    }

    public void SetJsonList<T>(string key, IEnumerable<T> items) => SetSetting(key, JsonSerializer.Serialize(items.ToList()));
}
