# BAMF one-click installer/updater
# Usage: double-click Update-BAMF.bat (runs this elevated), or:
#   powershell -ExecutionPolicy Bypass -File update.ps1 [-ZipPath C:\path\to\BAMF.zip]
#
# What it does:
#   1. Finds the newest BAMF*.zip in your Downloads (or uses -ZipPath)
#   2. Stops the BAMF service / process
#   3. Extracts the source to a TEMP folder and builds straight to C:\BAMFApp
#      - your appsettings.json is preserved (new defaults saved as appsettings.new.json)
#      - your database is snapshotted to C:\BAMFApp\backups first (last 10 kept)
#   4. Cleans up the temp source, restarts the service (creates it if missing)
#
# The only folder that exists afterwards is C:\BAMFApp. You never need to
# extract the zip yourself.

param([string]$ZipPath)

$ErrorActionPreference = "Stop"
$AppDir  = "C:\BAMFApp"
$Service = "BAMF"

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

$tempSrc = Join-Path $env:TEMP "bamf-src"

try {
    # --- locate the zip ---
    if (-not $ZipPath) {
        $downloads = Join-Path $env:USERPROFILE "Downloads"
        $zip = Get-ChildItem -Path $downloads -Filter "BAMF*.zip" -File -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $zip) { throw "No BAMF*.zip found in $downloads. Download the update zip first, or pass -ZipPath." }
        $ZipPath = $zip.FullName
    }
    if (-not (Test-Path $ZipPath)) { throw "Zip not found: $ZipPath" }
    Step "Using package: $ZipPath"

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw ".NET SDK not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0"
    }

    # --- stop whatever is running ---
    $svc = Get-Service -Name $Service -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne "Stopped") {
        Step "Stopping service $Service"
        Stop-Service -Name $Service -Force
        $svc.WaitForStatus("Stopped", (New-TimeSpan -Seconds 30))
    }
    Get-Process -Name "BAMF" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    # --- extract source to temp ---
    if (Test-Path $tempSrc) { Remove-Item $tempSrc -Recurse -Force }
    Step "Extracting source (temporary)"
    Expand-Archive -Path $ZipPath -DestinationPath $tempSrc -Force
    $csproj = Get-ChildItem -Path $tempSrc -Filter "BAMF.csproj" -Recurse | Select-Object -First 1
    if (-not $csproj) { throw "BAMF.csproj not found inside the zip - is this the right package?" }
    $srcDir = $csproj.Directory.FullName

    # --- preserve config ---
    $userConfig = Join-Path $AppDir "appsettings.json"
    $configBackup = $null
    if (Test-Path $userConfig) {
        $configBackup = Join-Path $env:TEMP "bamf-appsettings-backup.json"
        Copy-Item $userConfig $configBackup -Force
        Step "Preserving your existing appsettings.json"
    }

    # --- snapshot the database ---
    $db = Join-Path $AppDir "bamf.db"
    if (Test-Path $db) {
        $bakDir = Join-Path $AppDir "backups"
        if (-not (Test-Path $bakDir)) { New-Item -ItemType Directory -Path $bakDir | Out-Null }
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        Copy-Item $db (Join-Path $bakDir "bamf-$stamp.db") -Force
        Step "Database backed up to backups\bamf-$stamp.db"
        Get-ChildItem $bakDir -Filter "bamf-*.db" | Sort-Object LastWriteTime -Descending |
            Select-Object -Skip 10 | Remove-Item -Force
    }

    # --- build straight into the app folder ---
    Step "Building (this can take a couple of minutes)"
    Push-Location $srcDir
    try {
        dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true -o $AppDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    }
    finally { Pop-Location }

    # --- restore user config, keep the shipped one for reference ---
    if ($configBackup) {
        Copy-Item $userConfig (Join-Path $AppDir "appsettings.new.json") -Force
        Copy-Item $configBackup $userConfig -Force
        Step "Restored your appsettings.json (new defaults saved as appsettings.new.json)"
    }

    # --- keep the updater tools fresh inside the app folder ---
    $toolSrc = Join-Path $srcDir "update"
    if (Test-Path $toolSrc) {
        Copy-Item (Join-Path $toolSrc "*") $AppDir -Force -Exclude "Install-DesktopIcon.bat"
    }

    # --- service ---
    $svc = Get-Service -Name $Service -ErrorAction SilentlyContinue
    if (-not $svc) {
        Step "Service not found - creating it"
        sc.exe create $Service binPath= "$AppDir\BAMF.exe" start= auto obj= "NT AUTHORITY\LocalService" | Out-Null
        sc.exe description $Service "Basic ARP Monitoring Framework" | Out-Null
    }
    Step "Starting service $Service"
    Start-Service -Name $Service

    Write-Host ""
    Write-Host "BAMF updated successfully." -ForegroundColor Green
    Write-Host "Dashboard: http://localhost:8840  (Ctrl+F5 in your browser to load the new UI)"
    Write-Host "Everything lives in $AppDir - the old C:\BAMF source folder is no longer used and can be deleted."
}
catch {
    Write-Host ""
    Write-Host "UPDATE FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Check the service with: sc.exe query BAMF"
    exit 1
}
finally {
    if (Test-Path $tempSrc) { Remove-Item $tempSrc -Recurse -Force -ErrorAction SilentlyContinue }
}
