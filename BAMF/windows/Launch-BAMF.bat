@echo off
:: BAMF launcher - opens the dashboard, starting the service first if needed.
:: If the service is already running, no admin prompt appears. It opens the
:: address in Urls in the service's appsettings.json, so a changed port is
:: followed (8840 if it isn't set).

powershell -NoProfile -Command ^
  "$s = Get-Service -Name BAMF -ErrorAction SilentlyContinue;" ^
  "if (-not $s) { [System.Windows.Forms.MessageBox] | Out-Null; Write-Host 'BAMF service not installed - run the updater first.'; Start-Sleep 5; exit 1 };" ^
  "if ($s.Status -ne 'Running') {" ^
  "  Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile -Command Start-Service BAMF';" ^
  "  (Get-Service BAMF).WaitForStatus('Running', (New-TimeSpan -Seconds 20));" ^
  "};" ^
  "$url = 'http://localhost:8840'; $q = [char]34;" ^
  "$w = Get-CimInstance Win32_Service | Where-Object Name -eq 'BAMF';" ^
  "if ($w) {" ^
  "  $cfg = Join-Path (Split-Path ((($w.PathName -replace $q, '') -replace '(?i)\.exe.*$', '.exe'))) 'appsettings.json';" ^
  "  if (Test-Path $cfg) {" ^
  "    $m = [regex]::Match((Get-Content -Raw $cfg), $q + 'Urls' + $q + '\s*:\s*' + $q + '([^' + $q + ';]+)');" ^
  "    if ($m.Success) { $url = $m.Groups[1].Value -replace '://(0\.0\.0\.0|\*|\+|\[::\])', '://localhost' }" ^
  "  }" ^
  "};" ^
  "Start-Process $url"
