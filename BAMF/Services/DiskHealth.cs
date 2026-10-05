namespace LanWatch.Services;

/// <summary>
/// How much room BAMF has: the database's size, the backups', how much is free on the disk they live on (and on the second
/// copy's, if one is set), and an alert when that gets low. BAMF runs unattended; a disk that filled up used to show only as backups
/// failing or a crash. Free space is read, nothing is written, and only the drives BAMF's own files are on are looked at.
/// </summary>
public sealed class DiskHealth : BackgroundService
{
    public const int DefaultPercent = 10, MinPercent = 2, MaxPercent = 50;
    /// <summary>Whatever the percentage says, less than this free is low: a big disk at 12% free can still have only a few hundred megabytes.</summary>
    public const long MinFreeBytes = 500L * 1024 * 1024;
    public static readonly TimeSpan Repeat = TimeSpan.FromHours(24);

    public sealed record Volume(string Role, string Path, long TotalBytes, long FreeBytes, double FreePercent, bool Low);
    public sealed record Report(DateTime AtUtc, long DatabaseBytes, long BackupBytes, int BackupCount, long HistoryRows, IReadOnlyList<Volume> Volumes);

    private readonly HostStore _store;
    private readonly NightlyBackup _backup;
    private readonly ScannerService? _scanner;
    private readonly ILogger<DiskHealth> _log;
    private readonly Func<string, (long Total, long Free)?> _space;
    private readonly Dictionary<string, DateTime> _alerted = new();      // volume path -> when it was last said to be low
    private readonly object _lock = new();

    public DiskHealth(HostStore store, NightlyBackup backup, ScannerService? scanner, ILogger<DiskHealth> log, Func<string, (long Total, long Free)?>? space = null)
    {
        _store = store; _backup = backup; _scanner = scanner; _log = log; _space = space ?? Space;
    }

    /// <summary>On unless switched off in Settings.</summary>
    public bool AlertEnabled => _store.GetSetting("diskAlert") != "false";
    public int Percent => int.TryParse(_store.GetSetting("diskAlertPercent"), out var p) && p is >= MinPercent and <= MaxPercent ? p : DefaultPercent;

    /// <summary>Total and free bytes on the drive a path is on: the drive whose root is the longest match, so a mount point wins over "/".</summary>
    internal static (long Total, long Free)? Space(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            DriveInfo? best = null;
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                var root = d.RootDirectory.FullName;
                if (full.StartsWith(root, cmp) && (best is null || root.Length > best.RootDirectory.FullName.Length)) best = d;
            }
            return best is null ? null : (best.TotalSize, best.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    public static bool IsLow(long total, long free, int percent) => total > 0 && (free < MinFreeBytes || free * 100.0 / total < percent);

    private static long Size(string path) { try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch (IOException) { return 0; } }

    public Report Snapshot(DateTime? nowUtc = null)
    {
        var db = _store.DatabasePath;
        var dbBytes = Size(db) + Size(db + "-wal") + Size(db + "-shm");
        var backups = _backup.Backups();
        var backupBytes = backups.Sum(f => f.Length);
        var volumes = new List<Volume>();
        void Add(string role, string path)
        {
            if (_space(path) is not { } sp) return;
            // The same drive twice (a "second copy" on the same disk) is said once.
            if (volumes.Any(v => v.TotalBytes == sp.Total && v.FreeBytes == sp.Free && string.Equals(System.IO.Path.GetPathRoot(v.Path), System.IO.Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase))) return;
            var pct = sp.Total > 0 ? sp.Free * 100.0 / sp.Total : 0;
            volumes.Add(new Volume(role, path, sp.Total, sp.Free, Math.Round(pct, 1), IsLow(sp.Total, sp.Free, Percent)));
        }
        Add("BAMF's data and backups", System.IO.Path.GetDirectoryName(db) ?? ".");
        if (_backup.CopyTo is { } copy) Add("The second copy of the backups", copy);
        long rows = 0;
        try { rows = _store.CountEvents(); } catch (Exception ex) { _log.LogDebug(ex, "Couldn't count history rows"); }
        return new Report(nowUtc ?? DateTime.UtcNow, dbBytes, backupBytes, backups.Count, rows, volumes);
    }

    /// <summary>
    /// What to say now: a low drive once, again each day it stays low, and a drive that has room again after it was said to be low.
    /// Nothing at all when the alert is switched off.
    /// </summary>
    internal List<(string Title, string Detail)> Evaluate(Report r, DateTime nowUtc)
    {
        var say = new List<(string, string)>();
        if (!AlertEnabled) return say;
        lock (_lock)
            foreach (var v in r.Volumes)
            {
                var was = _alerted.TryGetValue(v.Path, out var at);
                if (v.Low && (!was || nowUtc - at >= Repeat))
                {
                    _alerted[v.Path] = nowUtc;
                    say.Add(($"Disk space is low for BAMF: {Gb(v.FreeBytes)} free",
                        $"{v.Role} are on a drive with {Gb(v.FreeBytes)} free of {Gb(v.TotalBytes)} ({v.FreePercent:0.#}%). BAMF's database is {Mb(r.DatabaseBytes)} and its backups {Mb(r.BackupBytes)}. " +
                        "When the disk fills, backups fail and BAMF can stop recording. Free some space, keep fewer backups, or lower the history retention."));
                }
                else if (!v.Low && was)
                {
                    _alerted.Remove(v.Path);
                    say.Add(("BAMF has room on its disk again", $"{v.Role} are on a drive with {Gb(v.FreeBytes)} free ({v.FreePercent:0.#}%)."));
                }
            }
        return say;
    }

    private static string Gb(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.#} GB" : $"{b / (double)(1L << 20):0} MB";
    private static string Mb(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.#} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.#} MB" : $"{b / 1024.0:0} KB";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var (title, detail) in Evaluate(Snapshot(), DateTime.UtcNow))
                    if (_scanner is not null) await _scanner.RaiseSecurity(title, detail, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "The disk space check failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(10), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
