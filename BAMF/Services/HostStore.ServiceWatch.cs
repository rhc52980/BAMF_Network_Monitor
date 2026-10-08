using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// The service watch's two tables: the things being watched (a web page or a port on one device), and a reading from each check, kept for
/// three days so the card can draw its last 24 hours.
/// </summary>
public partial class HostStore
{
    public sealed record WatchedService(long Id, long HostId, string Name, string Kind, int Port, string Path, bool Https, int SlowMs, string Created);
    public sealed record ServiceCheck(string At, bool Ok, int? Ms, string? Error, bool DeviceOff);

    private static void InitServiceWatch(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS watched_services (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                host_id INTEGER NOT NULL,
                name    TEXT NOT NULL,
                kind    TEXT NOT NULL,
                port    INTEGER NOT NULL,
                path    TEXT NOT NULL DEFAULT '/',
                https   INTEGER NOT NULL DEFAULT 0,
                slow_ms INTEGER NOT NULL DEFAULT 0,
                created TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS service_checks (
                service_id INTEGER NOT NULL,
                at         TEXT NOT NULL,
                ok         INTEGER NOT NULL,
                ms         INTEGER,
                error      TEXT,
                device_off INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_service_checks ON service_checks (service_id, at);
            """;
        cmd.ExecuteNonQuery();
    }

    private static WatchedService ReadService(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetInt32(6) != 0, r.GetInt32(7), r.GetString(8));

    public List<WatchedService> GetServices()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, host_id, name, kind, port, path, https, slow_ms, created FROM watched_services ORDER BY id";
            using var r = cmd.ExecuteReader();
            var list = new List<WatchedService>();
            while (r.Read()) list.Add(ReadService(r));
            return list;
        }
    }

    /// <summary>Adds a service to watch. Null when that device, kind, port and path are already watched.</summary>
    public WatchedService? AddService(long hostId, string name, string kind, int port, string path, bool https, int slowMs)
    {
        lock (_lock)
        {
            using var conn = Open();
            using (var dup = conn.CreateCommand())
            {
                dup.CommandText = "SELECT COUNT(*) FROM watched_services WHERE host_id = $h AND kind = $k AND port = $p AND path = $path";
                dup.Parameters.AddWithValue("$h", hostId);
                dup.Parameters.AddWithValue("$k", kind);
                dup.Parameters.AddWithValue("$p", port);
                dup.Parameters.AddWithValue("$path", path);
                if (Convert.ToInt64(dup.ExecuteScalar()) > 0) return null;
            }
            var now = DateTime.UtcNow.ToString("o");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO watched_services (host_id, name, kind, port, path, https, slow_ms, created) VALUES ($h, $n, $k, $p, $path, $s, $slow, $c);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$h", hostId);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$p", port);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$s", https ? 1 : 0);
            cmd.Parameters.AddWithValue("$slow", slowMs);
            cmd.Parameters.AddWithValue("$c", now);
            var id = Convert.ToInt64(cmd.ExecuteScalar());
            return new WatchedService(id, hostId, name, kind, port, path, https, slowMs, now);
        }
    }

    public bool DeleteService(long id)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM service_checks WHERE service_id = $i; DELETE FROM watched_services WHERE id = $i; SELECT changes();";
            cmd.Parameters.AddWithValue("$i", id);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
    }

    public void AddServiceCheck(long serviceId, DateTime atUtc, bool ok, int? ms, string? error, bool deviceOff)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO service_checks (service_id, at, ok, ms, error, device_off) VALUES ($i, $at, $ok, $ms, $e, $off)";
            cmd.Parameters.AddWithValue("$i", serviceId);
            cmd.Parameters.AddWithValue("$at", atUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$ok", ok ? 1 : 0);
            cmd.Parameters.AddWithValue("$ms", ms is { } m ? m : DBNull.Value);
            cmd.Parameters.AddWithValue("$e", error is null ? DBNull.Value : error.Length > 300 ? error[..300] : error);
            cmd.Parameters.AddWithValue("$off", deviceOff ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>One service's readings since a time, oldest first.</summary>
    public List<ServiceCheck> GetServiceChecks(long serviceId, DateTime sinceUtc)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT at, ok, ms, error, device_off FROM service_checks WHERE service_id = $i AND at >= $s ORDER BY at";
            cmd.Parameters.AddWithValue("$i", serviceId);
            cmd.Parameters.AddWithValue("$s", sinceUtc.ToString("o"));
            using var r = cmd.ExecuteReader();
            var list = new List<ServiceCheck>();
            while (r.Read())
                list.Add(new ServiceCheck(r.GetString(0), r.GetInt32(1) != 0, r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4) != 0));
            return list;
        }
    }

    /// <summary>Drops readings older than a time, and the readings of services that no longer exist.</summary>
    public void PruneServiceChecks(DateTime beforeUtc)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM service_checks WHERE at < $b OR service_id NOT IN (SELECT id FROM watched_services)";
            cmd.Parameters.AddWithValue("$b", beforeUtc.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }
}
