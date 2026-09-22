using System.Text;
using Microsoft.Data.Sqlite;

namespace LanWatch.Services;

/// <summary>
/// A copy of the whole database you can take away with you, and putting one
/// back. Taken while BAMF keeps running: VACUUM INTO writes a fresh, compacted
/// file from one read transaction, so the copy is whole even with a scan
/// landing halfway through - which copying a live SQLite file byte for byte
/// can't promise.
/// </summary>
public partial class HostStore
{
    /// <summary>Where copies of the database are kept: beside it, as the updaters do.</summary>
    public string BackupsDir => Path.Combine(Path.GetDirectoryName(_dbPath) ?? ".", "backups");

    /// <summary>Writes a snapshot into <paramref name="dir"/> and returns its path.</summary>
    public string SnapshotTo(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"bamf-backup-{Guid.NewGuid():N}.db");
        // Under the store's lock, like every other access: the database isn't
        // in WAL mode, so a writer arriving mid-copy would otherwise find it
        // busy rather than waiting its turn.
        lock (_lock) Vacuum(path);
        return path;
    }

    /// <summary>
    /// Puts a backup back. The file has to be a SQLite database that passes an
    /// integrity check and has BAMF's tables; the database it replaces is kept
    /// in <see cref="BackupsDir"/> first. The swap happens under the store's
    /// lock, so no scan writes into either file halfway, and the schema set-up
    /// runs again afterwards, so a backup from an older BAMF gains whatever
    /// tables and columns it's missing. Returns the name the old database was
    /// kept under, or why the file was turned down.
    /// </summary>
    public (string? Kept, string? Error) RestoreFrom(string file)
    {
        var why = CheckBackup(file);
        if (why is not null) return (null, why);
        Directory.CreateDirectory(BackupsDir);
        // The backup goes beside the database first, so the swap itself is a
        // rename on the same disk: done in one step, never half a file.
        // Copying straight over the live file could leave it half-written if
        // the copy failed partway, with a full disk say.
        var incoming = _dbPath + ".restoring";
        File.Copy(file, incoming, overwrite: true);
        try
        {
            lock (_lock)
            {
                // Named to the second, with -2, -3 on the end for a second restore
                // inside the same second.
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var kept = Path.Combine(BackupsDir, $"bamf-before-restore-{stamp}.db");
                for (var n = 2; File.Exists(kept); n++) kept = Path.Combine(BackupsDir, $"bamf-before-restore-{stamp}-{n}.db");
                Vacuum(kept);
                // Pooled connections keep the file open, and Windows won't replace
                // a file that's open.
                SqliteConnection.ClearAllPools();
                try
                {
                    File.Move(incoming, _dbPath, overwrite: true);
                    // A journal left beside the old file would be played back into
                    // the new one.
                    foreach (var extra in new[] { "-journal", "-wal", "-shm" })
                        if (File.Exists(_dbPath + extra)) File.Delete(_dbPath + extra);
                    Init();
                }
                catch
                {
                    // Whatever went wrong after the swap, put back what was there.
                    SqliteConnection.ClearAllPools();
                    File.Copy(kept, _dbPath, overwrite: true);
                    Init();
                    throw;
                }
                return (Path.GetFileName(kept), null);
            }
        }
        finally
        {
            // Gone after a good swap; still here if anything stopped it first.
            if (File.Exists(incoming)) File.Delete(incoming);
        }
    }

    private void Vacuum(string path)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $path";
        cmd.Parameters.AddWithValue("$path", path);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Why a file can't be restored, or null if it can.</summary>
    private static string? CheckBackup(string file)
    {
        var head = new byte[16];
        using (var fs = File.OpenRead(file))
            if (fs.Read(head, 0, head.Length) < head.Length) return "That file is too small to be a BAMF backup.";
        if (Encoding.ASCII.GetString(head, 0, 15) != "SQLite format 3")
            return "That isn't a BAMF backup: it isn't a database file at all.";
        try
        {
            // Not pooled, so nothing keeps the upload open once it's checked.
            using var conn = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA integrity_check";
                if (cmd.ExecuteScalar() as string != "ok") return "That backup is damaged: SQLite's integrity check didn't pass.";
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('hosts', 'settings', 'events')";
                if (Convert.ToInt32(cmd.ExecuteScalar()) < 3) return "That's a database, but not a BAMF one: it doesn't have BAMF's tables.";
            }
        }
        catch (SqliteException) { return "That file couldn't be opened as a database."; }
        return null;
    }
}
