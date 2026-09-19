using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// The TLS certificates BAMF read off devices' HTTPS ports, for the
/// certificate watch and the hygiene card. One row per device and port,
/// replaced each time it's checked; a check that fails keeps the last good
/// certificate and records why.
/// </summary>
public partial class HostStore
{
    public sealed record CertRow(long HostId, int Port, string Subject, string Issuer, string NotAfter,
        bool SelfSigned, string Error, string CheckedAt, string Alerted);

    private static void InitSecurity(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS certs (
                host_id     INTEGER NOT NULL,
                port        INTEGER NOT NULL,
                subject     TEXT NOT NULL DEFAULT '',
                issuer      TEXT NOT NULL DEFAULT '',
                not_after   TEXT NOT NULL DEFAULT '',
                self_signed INTEGER NOT NULL DEFAULT 0,
                error       TEXT NOT NULL DEFAULT '',
                checked_at  TEXT NOT NULL,
                alerted     TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (host_id, port)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Records a certificate read off a device. The expiry alert stage resets
    /// when the certificate changes, so a renewed one can warn again next time.
    /// </summary>
    public void SaveCert(long hostId, int port, string subject, string issuer, string notAfter, bool selfSigned)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO certs (host_id, port, subject, issuer, not_after, self_signed, error, checked_at, alerted)
                VALUES ($h, $p, $s, $i, $n, $ss, '', $at, '')
                ON CONFLICT(host_id, port) DO UPDATE SET
                    subject = excluded.subject, issuer = excluded.issuer, self_signed = excluded.self_signed,
                    error = '', checked_at = excluded.checked_at,
                    alerted = CASE WHEN certs.not_after = excluded.not_after THEN certs.alerted ELSE '' END,
                    not_after = excluded.not_after
                """;
            cmd.Parameters.AddWithValue("$h", hostId);
            cmd.Parameters.AddWithValue("$p", port);
            cmd.Parameters.AddWithValue("$s", subject);
            cmd.Parameters.AddWithValue("$i", issuer);
            cmd.Parameters.AddWithValue("$n", notAfter);
            cmd.Parameters.AddWithValue("$ss", selfSigned ? 1 : 0);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>A check that failed: keeps whatever was read last time, and says why this one didn't work.</summary>
    public void SaveCertError(long hostId, int port, string error)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO certs (host_id, port, error, checked_at) VALUES ($h, $p, $e, $at)
                ON CONFLICT(host_id, port) DO UPDATE SET error = excluded.error, checked_at = excluded.checked_at
                """;
            cmd.Parameters.AddWithValue("$h", hostId);
            cmd.Parameters.AddWithValue("$p", port);
            cmd.Parameters.AddWithValue("$e", error.Length > 200 ? error[..200] : error);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkCertAlerted(long hostId, int port, string stage)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE certs SET alerted = $s WHERE host_id = $h AND port = $p";
            cmd.Parameters.AddWithValue("$s", stage);
            cmd.Parameters.AddWithValue("$h", hostId);
            cmd.Parameters.AddWithValue("$p", port);
            cmd.ExecuteNonQuery();
        }
    }

    public List<CertRow> GetCerts()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT host_id, port, subject, issuer, not_after, self_signed, error, checked_at, alerted FROM certs ORDER BY host_id, port";
            var list = new List<CertRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new CertRow(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.GetInt64(5) == 1, r.GetString(6), r.GetString(7), r.GetString(8)));
            return list;
        }
    }
}
