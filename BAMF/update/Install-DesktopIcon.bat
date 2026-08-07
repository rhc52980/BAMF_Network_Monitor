@echo off
:: Creates a "BAMF" shortcut on your Desktop with the burst icon.
:: Copies the launcher + icon into C:\BAMFApp first so the shortcut
:: keeps working even when the C:\BAMF source folder is replaced by updates.

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$app = 'C:\BAMFApp';" ^
  "if (-not (Test-Path $app)) { New-Item -ItemType Directory -Path $app | Out-Null };" ^
  "Copy-Item -Path (Join-Path '%~dp0' 'Launch-BAMF.bat') -Destination $app -Force;" ^
  "Copy-Item -Path (Join-Path '%~dp0' 'bamf.ico') -Destination $app -Force;" ^
  "$desktop = [Environment]::GetFolderPath('Desktop');" ^
  "$ws = New-Object -ComObject WScript.Shell;" ^
  "$sc = $ws.CreateShortcut((Join-Path $desktop 'BAMF.lnk'));" ^
  "$sc.TargetPath = Join-Path $app 'Launch-BAMF.bat';" ^
  "$sc.WorkingDirectory = $app;" ^
  "$sc.IconLocation = (Join-Path $app 'bamf.ico') + ',0';" ^
  "$sc.Description = 'BAMF - Basic ARP Monitoring Framework';" ^
  "$sc.Save();" ^
  "Write-Host 'Desktop shortcut created.' -ForegroundColor Green"
pause
