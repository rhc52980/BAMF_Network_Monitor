@echo off
:: BAMF one-click updater - double-click me after downloading a new BAMF.zip
:: Elevates to admin, then runs update.ps1 from the same folder.
::
:: Installing for the first time? Install-BAMF.bat runs this same script and
:: creates the service if it doesn't exist yet.

powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -NoExit -File \"%~dp0update.ps1\"'"
