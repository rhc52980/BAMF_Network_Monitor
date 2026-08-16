@echo off
:: BAMF first-time installer - double-click me.
:: Builds from this source folder into C:\BAMF, creates the BAMF Windows
:: service, and starts it. Requires the .NET 8 SDK on this machine.
::
:: This runs the same script as Update-BAMF.bat - installing and updating are
:: the same operation. Use whichever name matches what you're doing.

powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -NoExit -File \"%~dp0update.ps1\"'"
