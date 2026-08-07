@echo off
:: BAMF one-click updater - double-click me after downloading a new BAMF.zip
:: Elevates to admin, then runs update.ps1 from the same folder.

powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -NoExit -File \"%~dp0update.ps1\"'"
