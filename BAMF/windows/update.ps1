# BAMF one-click installer/updater
# Usage: double-click Update-BAMF.bat (runs this elevated), or:
#   powershell -ExecutionPolicy Bypass -File update.ps1 [-ZipPath C:\path\to\BAMF.zip]
#
# What it does:
#   1. Finds the source: -ZipPath, else this script's own source tree, else the
#      newest BAMF*.zip in Downloads
#   2. Stops the BAMF service / process
#   3. Migrates an old C:\BAMFApp install to C:\BAMF (folder move + service
#      repoint + desktop shortcut fixup), once
#   4. Builds straight into the install folder
#      - your appsettings.json is preserved (new defaults saved as appsettings.new.json)
#      - your database is snapshotted to <install>\backups first (last 30 kept)
#   5. Cleans up the temp source, restarts the service (creates it if missing)
#
# The only folder that exists afterwards is C:\BAMF. You never need to extract
# the zip yourself.

param([string]$ZipPath)

$ErrorActionPreference = "Stop"
# Needed to read a version out of a package without extracting it. Windows
# PowerShell 5.1 doesn't load this by default.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$AppDir  = "C:\BAMF"      # default for a fresh install; an existing service wins
$LegacyDir = "C:\BAMFApp" # pre-1.2.2 name, migrated automatically
$Service = "BAMF"
$RepointService = $false

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

$tempSrc = Join-Path $env:TEMP "bamf-src"

try {
    # --- where does the installed service actually run from? ---
    # Older installs live somewhere other than C:\BAMFApp. Building into the
    # default while the service still points elsewhere used to "succeed" and
    # change nothing, so follow the service instead. Updating its own folder
    # also keeps that install's appsettings.json and bamf.db in place.
    $svcInfo = Get-CimInstance Win32_Service -Filter "Name='$Service'" -ErrorAction SilentlyContinue
    if ($svcInfo -and $svcInfo.PathName) {
        $binPath = $svcInfo.PathName.Trim()
        # binPath may be quoted and may carry arguments
        $exePath = if ($binPath.StartsWith('"')) { ($binPath -split '"')[1] } else { ($binPath -split ' ')[0] }
        $existingDir = Split-Path $exePath -Parent
        if ($existingDir -and (Test-Path $existingDir)) {
            if ($existingDir -ne $AppDir) {
                Step "Service '$Service' runs from $existingDir - updating that folder (keeps its config and database)"
            }
            $AppDir = $existingDir
        }
        else {
            Step "Service '$Service' points at a missing path ($exePath) - installing to $AppDir and repointing it"
            $RepointService = $true
        }
    }

    # --- locate the source ---
    # Priority: an explicit -ZipPath, then the source tree this script lives in,
    # then the newest BAMF*.zip in Downloads. The middle case matters: running
    # update.ps1 out of a freshly downloaded tree used to skip that tree
    # entirely and rebuild from whatever stale zip was sitting in Downloads,
    # which looks like a successful update that installs old code.
    # linux/install.sh has always preferred its own tree; this matches it.
    $srcDir = $null
    if (-not $ZipPath -and $PSScriptRoot) {
        $treeRoot = Split-Path $PSScriptRoot -Parent   # ...\BAMF\update -> ...\BAMF
        if ($treeRoot -and (Test-Path (Join-Path $treeRoot "BAMF.csproj"))) {
            $srcDir = $treeRoot
            Step "Using source tree: $srcDir"
        }
    }

    if (-not $srcDir) {
        if (-not $ZipPath) {
            $downloads = Join-Path $env:USERPROFILE "Downloads"
            $candidates = @(Get-ChildItem -Path $downloads -Filter "BAMF*.zip" -File -ErrorAction SilentlyContinue)
            if ($candidates.Count -eq 0) { throw "No BAMF*.zip found in $downloads. Download the update zip first, or pass -ZipPath." }

            # Pick the HIGHEST VERSION, not the newest file. Downloading an older
            # package after a newer one used to win purely on timestamp, which is
            # how a stale zip can quietly reinstall old code.
            $ranked = foreach ($c in $candidates) {
                $raw = $null
                # The version declared inside the package is authoritative - a
                # filename can say anything.
                try {
                    $z = [System.IO.Compression.ZipFile]::OpenRead($c.FullName)
                    try {
                        $entry = $z.Entries | Where-Object { $_.FullName -like "*BAMF.csproj" } | Select-Object -First 1
                        if ($entry) {
                            $sr = New-Object System.IO.StreamReader($entry.Open())
                            $text = $sr.ReadToEnd(); $sr.Close()
                            $m = [regex]::Match($text, '<Version>\s*([^<]+?)\s*</Version>')
                            if ($m.Success) { $raw = $m.Groups[1].Value }
                        }
                    }
                    finally { $z.Dispose() }
                }
                catch { }   # unreadable or not a zip: ranks last, never crashes the update

                # Fall back to a version in the filename, e.g. BAMF-1.5.0.zip
                if (-not $raw) {
                    $fm = [regex]::Match($c.Name, '(\d+\.\d+(?:\.\d+)?)')
                    if ($fm.Success) { $raw = $fm.Groups[1].Value }
                }

                $parsed = [version]"0.0.0"
                if ($raw) { [void][version]::TryParse($raw, [ref]$parsed) }
                [pscustomobject]@{ File = $c; Version = $parsed; Raw = $raw }
            }

            # Highest version wins; same version falls back to the newer file.
            $ordered = $ranked | Sort-Object -Property Version, { $_.File.LastWriteTime } -Descending
            $best = $ordered | Select-Object -First 1
            $ZipPath = $best.File.FullName

            if ($ordered.Count -gt 1) {
                Step "Found $($ordered.Count) packages in Downloads; choosing the highest version"
                foreach ($o in $ordered) {
                    $mark = if ($o.File.FullName -eq $ZipPath) { '->' } else { '  ' }
                    $shown = if ($o.Raw) { $o.Raw } else { 'unknown' }
                    Write-Host ("     {0} {1,-28} version {2}" -f $mark, $o.File.Name, $shown)
                }
            }
        }
        if (-not (Test-Path $ZipPath)) { throw "Zip not found: $ZipPath" }
        Step "Using package: $ZipPath  (downloaded $((Get-Item $ZipPath).LastWriteTime))"
    }

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

    # --- one-time migration: C:\BAMFApp -> C:\BAMF ---
    # Moves the whole folder, so config, database and backups come along intact.
    # Only runs when the install actually lives in the old location.
    # Covers both cases: the service points at the old folder, and the service is
    # missing but an old install is sitting there with its database.
    $legacyHasInstall = (Test-Path (Join-Path $LegacyDir "bamf.db")) -or (Test-Path (Join-Path $LegacyDir "BAMF.exe"))
    if ($legacyHasInstall -and ($AppDir -ieq $LegacyDir -or -not (Test-Path (Join-Path $AppDir "BAMF.exe")))) {
        $canMove = $true
        if (Test-Path "C:\BAMF") {
            $existing = @(Get-ChildItem "C:\BAMF" -Force -ErrorAction SilentlyContinue)
            if ($existing.Count -eq 0) {
                # Directory.Delete without recursion throws unless it's genuinely
                # empty, so this can never take data with it.
                [IO.Directory]::Delete("C:\BAMF")
            }
            elseif (Test-Path "C:\BAMF\BAMF.exe") {
                # Another install already lives there - don't guess which one is wanted.
                Step "C:\BAMF already contains an install; keeping this one in $LegacyDir"
                $canMove = $false
            }
            else {
                # Almost certainly the abandoned pre-1.1 source folder. Set it aside
                # rather than delete it, so nothing is lost if that's wrong.
                $aside = "C:\BAMF.old-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
                Step "C:\BAMF exists but isn't an install - moving it to $aside"
                Move-Item "C:\BAMF" $aside -Force
            }
        }

        if ($canMove) {
            Step "Migrating install from $LegacyDir to C:\BAMF (config, database and backups move with it)"
            Move-Item $LegacyDir "C:\BAMF" -Force
            $AppDir = "C:\BAMF"
            $RepointService = $true

            # The desktop shortcut points into the old folder; retarget it.
            $lnk = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) "BAMF.lnk"
            if (-not (Test-Path $lnk)) {
                $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) "BAMF.lnk"
            }
            if (Test-Path $lnk) {
                try {
                    $ws = New-Object -ComObject WScript.Shell
                    $sc = $ws.CreateShortcut($lnk)
                    $sc.TargetPath = $sc.TargetPath -replace [regex]::Escape($LegacyDir), "C:\BAMF"
                    $sc.IconLocation = $sc.IconLocation -replace [regex]::Escape($LegacyDir), "C:\BAMF"
                    $sc.WorkingDirectory = "C:\BAMF"
                    $sc.Save()
                    Step "Desktop shortcut retargeted to C:\BAMF"
                }
                catch { Step "Could not update the desktop shortcut - re-run Install-DesktopIcon.bat" }
            }
        }
    }

    # --- extract source to temp (only when building from a zip) ---
    if (-not $srcDir) {
        if (Test-Path $tempSrc) { Remove-Item $tempSrc -Recurse -Force }
        Step "Extracting source (temporary)"
        Expand-Archive -Path $ZipPath -DestinationPath $tempSrc -Force
        $csproj = Get-ChildItem -Path $tempSrc -Filter "BAMF.csproj" -Recurse | Select-Object -First 1
        if (-not $csproj) { throw "BAMF.csproj not found inside the zip - is this the right package?" }
        $srcDir = $csproj.Directory.FullName
    }

    # Say up front which version is about to be built. A source too old to
    # declare one is the tell that you're rebuilding a stale package.
    $srcVersion = "unknown (source predates versioning)"
    $vm = [regex]::Match((Get-Content (Join-Path $srcDir "BAMF.csproj") -Raw), '<Version>\s*([^<]+?)\s*</Version>')
    if ($vm.Success) { $srcVersion = $vm.Groups[1].Value }
    $installedVersion = $null
    if (Test-Path (Join-Path $AppDir "BAMF.exe")) {
        $pv = (Get-Item (Join-Path $AppDir "BAMF.exe")).VersionInfo.ProductVersion
        if ($pv) { $installedVersion = ($pv -split '\+')[0].Trim() }
    }
    Step "Installing version $srcVersion$(if ($installedVersion) { " (replacing $installedVersion)" })"

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
            Select-Object -Skip 30 | Remove-Item -Force
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
    $toolSrc = Join-Path $srcDir "windows"
    if (Test-Path $toolSrc) {
        Copy-Item (Join-Path $toolSrc "*") $AppDir -Force -Exclude "Install-DesktopIcon.bat"
    }

    # --- prove the build actually produced what we expect ---
    $newExe = Join-Path $AppDir "BAMF.exe"
    if (-not (Test-Path $newExe)) { throw "Build finished but $newExe is missing - nothing was installed." }
    if (-not (Test-Path (Join-Path $AppDir "wwwroot\index.html"))) {
        throw "Build finished but $AppDir\wwwroot is missing - the dashboard would not load."
    }

    # --- service ---
    $svc = Get-Service -Name $Service -ErrorAction SilentlyContinue
    if (-not $svc) {
        Step "Service not found - creating it"
        sc.exe create $Service binPath= "$newExe" start= auto obj= "NT AUTHORITY\LocalService" | Out-Null
        sc.exe description $Service "Basic ARP Monitoring Framework" | Out-Null
    }
    elseif ($RepointService) {
        Step "Repointing service $Service at $newExe"
        sc.exe config $Service binPath= "$newExe" | Out-Null
    }
    Step "Starting service $Service"
    Start-Service -Name $Service

    # --- report what is now actually running ---
    # The whole point: an update that changes nothing must not claim success.
    $ver = (Get-Item $newExe).VersionInfo.ProductVersion
    if ($ver) { $ver = ($ver -split '\+')[0].Trim() }
    $runningFrom = $newExe
    $svcNow = Get-CimInstance Win32_Service -Filter "Name='$Service'" -ErrorAction SilentlyContinue
    if ($svcNow -and $svcNow.PathName) {
        $bp = $svcNow.PathName.Trim()
        $runningFrom = if ($bp.StartsWith('"')) { ($bp -split '"')[1] } else { ($bp -split ' ')[0] }
    }

    Write-Host ""
    if ($runningFrom -ne $newExe) {
        Write-Host "WARNING: the service runs $runningFrom but this update installed to $newExe." -ForegroundColor Yellow
        Write-Host "Those are different folders, so the update will not take effect. Fix with:"
        Write-Host "  sc.exe config $Service binPath= `"$newExe`""
    }
    else {
        Write-Host "BAMF $ver updated successfully." -ForegroundColor Green
        Write-Host "Running from: $runningFrom"
        Write-Host "Dashboard: http://localhost:8840  (Ctrl+F5 in your browser to load the new UI)"
        Write-Host "Confirm the version shown in the dashboard header matches $ver."
    }
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
