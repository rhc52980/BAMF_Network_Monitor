using System.Text.Json;

namespace LanWatch.Services;

/// <summary>
/// A backup every night, without a scheduled task: at the hour set in
/// Settings → System (3 am unless changed), a snapshot of the database goes into
/// the backups folder beside it as nightly-YYYYMMDD.db, and the oldest are
/// deleted past the number to keep. The updaters prune bamf-*.db, so they never
/// touch these. With Bamf:Backup:CopyTo set in appsettings.json, each one is
/// copied there too, a NAS share say, so a disk failure doesn't take the
/// backups with it; where BAMF writes files is the file's to say, not the
/// dashboard's. A backup that fails is an alert.
///
/// On by default, but off in the Home Assistant add-on, whose data is already
/// in Home Assistant's own backups.
/// </summary>
public sealed class NightlyBackup : BackgroundService
{
    public const string Prefix = "nightly-";
    public sealed record Result(string At, string? File, long Size, string? Copied, string? Error);

    private readonly HostStore _store;
    private readonly ScannerService _scanner;
    private readonly IConfiguration _config;
    private readonly ILogger<NightlyBackup> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);

    public NightlyBackup(HostStore store, ScannerService scanner, IConfiguration config, ILogger<NightlyBackup> log)
    {
        _store = store; _scanner = scanner; _config = config; _log = log;
    }

    private static bool InAddon => File.Exists(HomeAssistantAddon.OptionsPath);

    public bool Enabled => _store.GetSetting("backupNightly") is { } s ? s == "true" : !InAddon;
    public int Hour => int.TryParse(_store.GetSetting("backupHour"), out var h) && h is >= 0 and <= 23 ? h : 3;
    public int Keep => int.TryParse(_store.GetSetting("backupKeep"), out var k) && k is >= 1 and <= 60 ? k : 7;
    public string? CopyTo => _config["Bamf:Backup:CopyTo"] is { Length: > 0 } c ? c : null;

    public Result? Last
    {
        get
        {
            try { return JsonSerializer.Deserialize<Result>(_store.GetSetting("backupLast") ?? "null"); }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>The nightly backups there are, newest first.</summary>
    public List<FileInfo> Backups() => Directory.Exists(_store.BackupsDir)
        ? new DirectoryInfo(_store.BackupsDir).GetFiles(Prefix + "*.db").OrderByDescending(f => f.Name).ToList()
        : [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
            catch (OperationCanceledException) { break; }
            try
            {
                var now = DateTime.Now;
                if (Due(now, Enabled, Hour, File.Exists(Path.Combine(_store.BackupsDir, $"{Prefix}{now:yyyyMMdd}.db"))))
                    await Run(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning("Nightly backup: {Error}", ex.Message); }
        }
    }

    /// <summary>
    /// Whether to back up now: from the hour on, so a machine that was off at
    /// the hour catches up when it's back, and once a night, so not when
    /// today's is already there.
    /// </summary>
    public static bool Due(DateTime now, bool enabled, int hour, bool takenToday) => enabled && now.Hour >= hour && !takenToday;

    /// <summary>Takes tonight's backup now (or replaces it), prunes, copies, and records how it went.</summary>
    public async Task<Result> Run(CancellationToken ct)
    {
        await _busy.WaitAsync(ct);
        try
        {
            var dir = _store.BackupsDir;
            var name = $"{Prefix}{DateTime.Now:yyyyMMdd}.db";
            string? copied = null;
            Result result;
            try
            {
                // Written aside first, so a half-written file is never taken for a backup.
                var tmp = _store.SnapshotTo(dir);
                var path = Path.Combine(dir, name);
                try { File.Move(tmp, path, overwrite: true); }
                finally { if (File.Exists(tmp)) File.Delete(tmp); }
                // appsettings.json beside it, so a restore can get the networks,
                // the webhook and the passwords set there back too.
                var settings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                var settingsCopy = Path.ChangeExtension(path, ".appsettings.json");
                if (File.Exists(settings)) File.Copy(settings, settingsCopy, overwrite: true);
                Prune(dir, Keep);
                if (CopyTo is { } other)
                {
                    Directory.CreateDirectory(other);
                    var part = Path.Combine(other, name + ".part");
                    File.Copy(path, part, overwrite: true);
                    File.Move(part, Path.Combine(other, name), overwrite: true);
                    if (File.Exists(settingsCopy)) File.Copy(settingsCopy, Path.Combine(other, Path.GetFileName(settingsCopy)), overwrite: true);
                    Prune(other, Keep);
                    copied = other;
                }
                result = new Result(DateTime.UtcNow.ToString("o"), name, new FileInfo(path).Length, copied, null);
                _log.LogInformation("Nightly backup: {File}{Copied}", name, copied is null ? "" : $", copied to {copied}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                result = new Result(DateTime.UtcNow.ToString("o"), null, 0, null, ex.Message);
                _log.LogWarning("Nightly backup failed: {Error}", ex.Message);
                var title = "The nightly backup failed";
                var detail = $"BAMF couldn't back up its database tonight: {ex.Message} The last good backup is still there.";
                _store.AddAlert("backup", title, detail);
                await _scanner.SendGenericAlert(title, detail, "status", ct);
            }
            _store.SetSetting("backupLast", JsonSerializer.Serialize(result));
            return result;
        }
        finally { _busy.Release(); }
    }

    /// <summary>Keeps the newest few nightly backups in a folder, with their appsettings.json; nothing else there is touched.</summary>
    public static void Prune(string dir, int keep)
    {
        foreach (var old in new DirectoryInfo(dir).GetFiles(Prefix + "*.db").OrderByDescending(f => f.Name).Skip(keep))
        {
            old.Delete();
            var settings = Path.ChangeExtension(old.FullName, ".appsettings.json");
            if (File.Exists(settings)) File.Delete(settings);
        }
    }
}
