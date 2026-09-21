namespace LanWatch.Services;

/// <summary>
/// A copy of the whole database you can take away with you. Taken while BAMF
/// keeps running: VACUUM INTO writes a fresh, compacted file from one read
/// transaction, so the copy is whole even with a scan landing halfway through
/// - which copying a live SQLite file byte for byte can't promise.
/// </summary>
public partial class HostStore
{
    /// <summary>Writes a snapshot into <paramref name="dir"/> and returns its path.</summary>
    public string SnapshotTo(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"bamf-backup-{Guid.NewGuid():N}.db");
        // Under the store's lock, like every other access: the database isn't
        // in WAL mode, so a writer arriving mid-copy would otherwise find it
        // busy rather than waiting its turn.
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "VACUUM INTO $path";
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
        return path;
    }
}
