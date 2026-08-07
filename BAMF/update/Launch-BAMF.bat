@echo off
:: BAMF launcher - opens the dashboard, starting the service first if needed.
:: If the service is already running, no admin prompt appears.

powershell -NoProfile -Command ^
  "$s = Get-Service -Name BAMF -ErrorAction SilentlyContinue;" ^
  "if (-not $s) { [System.Windows.Forms.MessageBox] | Out-Null; Write-Host 'BAMF service not installed - run the updater first.'; Start-Sleep 5; exit 1 };" ^
  "if ($s.Status -ne 'Running') {" ^
  "  Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile -Command Start-Service BAMF';" ^
  "  (Get-Service BAMF).WaitForStatus('Running', (New-TimeSpan -Seconds 20));" ^
  "};" ^
  "Start-Process 'http://localhost:8840'"
