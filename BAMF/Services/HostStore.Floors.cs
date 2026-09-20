using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// Floor plans: an image of each floor, and where each device sits on one.
/// The images live in the database, so they're in every backup with
/// everything else. A device is on one floor at a time, at a spot given as a
/// share of the image's width and height, so it stays put whatever size the
/// plan is drawn at.
/// </summary>
public partial class HostStore
{
    public sealed record FloorRow(long Id, string Name, int Width, int Height, int Sort, string Updated, string Mime);

    /// <summary>What a floor drawn in BAMF is stored as, rather than an uploaded picture.</summary>
    public const string PlanMime = "application/vnd.bamf.plan+json";
    public sealed record FloorPlace(long HostId, long FloorId, double X, double Y);

    private static void InitFloors(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS floors (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                name    TEXT NOT NULL,
                mime    TEXT NOT NULL,
                image   BLOB NOT NULL,
                width   INTEGER NOT NULL,
                height  INTEGER NOT NULL,
                sort    INTEGER NOT NULL DEFAULT 0,
                updated TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS floor_places (
                host_id  INTEGER PRIMARY KEY,
                floor_id INTEGER NOT NULL,
                x        REAL NOT NULL,
                y        REAL NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public List<FloorRow> GetFloors()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, name, width, height, sort, updated, mime FROM floors ORDER BY sort, id";
            var list = new List<FloorRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new FloorRow(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5), r.GetString(6)));
            return list;
        }
    }

    public (string Mime, byte[] Data)? GetFloorImage(long id)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT mime, image FROM floors WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetString(0), (byte[])r["image"]) : null;
        }
    }

    public long AddFloor(string name, string mime, byte[] image, int width, int height)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO floors (name, mime, image, width, height, sort, updated)
                VALUES ($n, $m, $i, $w, $h, (SELECT COALESCE(MAX(sort), 0) + 1 FROM floors), $at);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$m", mime);
            cmd.Parameters.AddWithValue("$i", image);
            cmd.Parameters.AddWithValue("$w", width);
            cmd.Parameters.AddWithValue("$h", height);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>A new name, a new image, or both. The devices on it stay where they were.</summary>
    public bool UpdateFloor(long id, string? name, string? mime, byte[]? image, int width, int height)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = image is null
                ? "UPDATE floors SET name = COALESCE($n, name), updated = $at WHERE id = $id"
                : "UPDATE floors SET name = COALESCE($n, name), mime = $m, image = $i, width = $w, height = $h, updated = $at WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$n", (object?)name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            if (image is not null)
            {
                cmd.Parameters.AddWithValue("$m", mime!);
                cmd.Parameters.AddWithValue("$i", image);
                cmd.Parameters.AddWithValue("$w", width);
                cmd.Parameters.AddWithValue("$h", height);
            }
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Deletes a floor and takes its devices off it.</summary>
    public bool DeleteFloor(long id)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var places = conn.CreateCommand();
            places.Transaction = tx;
            places.CommandText = "DELETE FROM floor_places WHERE floor_id = $id";
            places.Parameters.AddWithValue("$id", id);
            places.ExecuteNonQuery();
            using var floor = conn.CreateCommand();
            floor.Transaction = tx;
            floor.CommandText = "DELETE FROM floors WHERE id = $id";
            floor.Parameters.AddWithValue("$id", id);
            var n = floor.ExecuteNonQuery();
            tx.Commit();
            return n > 0;
        }
    }

    public List<FloorPlace> GetFloorPlaces()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT host_id, floor_id, x, y FROM floor_places";
            var list = new List<FloorPlace>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new FloorPlace(r.GetInt64(0), r.GetInt64(1), r.GetDouble(2), r.GetDouble(3)));
            return list;
        }
    }

    /// <summary>Puts a device on a floor, or moves it; x and y are shares of the image, 0 to 1.</summary>
    public bool PlaceOnFloor(long hostId, long floorId, double x, double y)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var check = conn.CreateCommand();
            check.CommandText = "SELECT (SELECT COUNT(*) FROM floors WHERE id = $f) + (SELECT COUNT(*) FROM hosts WHERE id = $h)";
            check.Parameters.AddWithValue("$f", floorId);
            check.Parameters.AddWithValue("$h", hostId);
            if ((long)check.ExecuteScalar()! < 2) return false;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO floor_places (host_id, floor_id, x, y) VALUES ($h, $f, $x, $y)
                ON CONFLICT(host_id) DO UPDATE SET floor_id = excluded.floor_id, x = excluded.x, y = excluded.y
                """;
            cmd.Parameters.AddWithValue("$h", hostId);
            cmd.Parameters.AddWithValue("$f", floorId);
            cmd.Parameters.AddWithValue("$x", Math.Clamp(x, 0, 1));
            cmd.Parameters.AddWithValue("$y", Math.Clamp(y, 0, 1));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool RemoveFromFloor(long hostId)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM floor_places WHERE host_id = $h";
            cmd.Parameters.AddWithValue("$h", hostId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }
}
