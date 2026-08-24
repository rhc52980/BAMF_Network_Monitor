# BAMF scheduled backup
#
#   powershell -ExecutionPolicy Bypass -File Backup-BAMF.ps1
#   powershell -ExecutionPolicy Bypass -File Backup-BAMF.ps1 -Install   (elevated: register a nightly task)
#
# Takes a consistent snapshot of bamf.db and appsettings.json into the install
# folder's backups\, then prunes to the newest -Keep snapshots.
#
# Why it stops the service: the service writes to bamf.db continuously, and a
# plain file copy taken mid-write can be torn - SQLite may consider the result
# corrupt, and you would not find out until you tried to restore it. Stopping
# for the second it takes to copy is the difference between a backup and a file
# that looks like one. BAMF misses at most one scan cycle.

param(
    [string]$AppDir,
    [int]$Keep = 30,
    [switch]$NoStop,     # copy without stopping - fast, but the copy may be inconsistent
    [switch]$Install     # register a nightly scheduled task instead of backing up now
)

$ErrorActionPreference = "Stop"
$Service = "BAMF"

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# Follow the installed service rather than assuming a path, same as the updater.
function Resolve-AppDir {
    if ($AppDir) { return $AppDir }
    $svc = Get-CimInstance Win32_Service -Filter "Name='$Service'" -ErrorAction SilentlyContinue
    if ($svc -and $svc.PathName) {
        $bp = $svc.PathName.Trim()
        $exe = if ($bp.StartsWith('"')) { ($bp -split '"')[1] } else { ($bp -split ' ')[0] }
        $dir = Split-Path $exe -Parent
        if ($dir -and (Test-Path $dir)) { return $dir }
    }
    foreach ($guess in @("C:\BAMF", "C:\BAMFApp")) {
        if (Test-Path (Join-Path $guess "bamf.db")) { return $guess }
    }
    throw "Could not find a BAMF install. Pass -AppDir C:\path\to\BAMF."
}

if ($Install) {
    $me = $MyInvocation.MyCommand.Path
    $action  = New-ScheduledTaskAction -Execute "powershell.exe" `
        -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$me`""
    $trigger = New-ScheduledTaskTrigger -Daily -At 3am
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopOnIdleEnd
    Register-ScheduledTask -TaskName "BAMF Backup" -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force | Out-Null
    Write-Host ""
    Write-Host "Registered scheduled task 'BAMF Backup' - runs daily at 03:00 as SYSTEM." -ForegroundColor Green
    Write-Host "Run it now:     Start-ScheduledTask -TaskName 'BAMF Backup'"
    Write-Host "Remove it:      Unregister-ScheduledTask -TaskName 'BAMF Backup' -Confirm:`$false"
    exit 0
}

try {
    $dir = Resolve-AppDir
    $db  = Join-Path $dir "bamf.db"
    if (-not (Test-Path $db)) { throw "No database at $db - is this the right folder?" }

    $bak = Join-Path $dir "backups"
    if (-not (Test-Path $bak)) { New-Item -ItemType Directory -Path $bak | Out-Null }

    $svc = Get-Service -Name $Service -ErrorAction SilentlyContinue
    $wasRunning = $false
    if (-not $NoStop -and $svc -and $svc.Status -eq "Running") {
        $wasRunning = $true
        Step "Stopping $Service for a consistent copy"
        Stop-Service -Name $Service -Force
        $svc.WaitForStatus("Stopped", (New-TimeSpan -Seconds 30))
    }

    try {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        Copy-Item $db (Join-Path $bak "bamf-$stamp.db") -Force
        Step "Snapshot: backups\bamf-$stamp.db"

        # Config travels with it - it holds your subnets, webhook and password.
        $cfg = Join-Path $dir "appsettings.json"
        if (Test-Path $cfg) { Copy-Item $cfg (Join-Path $bak "appsettings.json") -Force }
    }
    finally {
        # Always restart, even if the copy failed.
        if ($wasRunning) { Start-Service -Name $Service; Step "Started $Service" }
    }

    $old = Get-ChildItem $bak -Filter "bamf-*.db" | Sort-Object LastWriteTime -Descending | Select-Object -Skip $Keep
    if ($old) {
        $old | Remove-Item -Force
        Step "Pruned $($old.Count) old snapshot(s), keeping $Keep"
    }

    Write-Host ""
    Write-Host "Backup complete: $bak" -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "BACKUP FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1   # non-zero so Task Scheduler shows the failure
}
