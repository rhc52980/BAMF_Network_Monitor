# BAMF on Windows — start here

Everything in this folder is for Windows. Double-click one of these:

| File | What it does |
|---|---|
| **`Install-BAMF.bat`** | **First time?** Start here. Builds to `C:\BAMF`, creates the `BAMF` Windows service, starts it. |
| **`Update-BAMF.bat`** | Later, to update. Same script — installing and updating are one operation. |
| `Install-DesktopIcon.bat` | Optional. Puts a BAMF shortcut on your Desktop. |
| `Backup-BAMF.ps1 -Install` | Optional. Registers a nightly database backup at 03:00. |

Both installer and updater need the **.NET 8 SDK** on this machine, because
they build from source here: https://dotnet.microsoft.com/download/dotnet/8.0

The other two files are machinery, not things to run directly:
`update.ps1` does the actual work, and `Launch-BAMF.bat` is what the Desktop
shortcut points at.

## Where things end up

Everything lives in **`C:\BAMF`** — the binary, `appsettings.json`, `bamf.db`,
and `backups/`. Nothing else is scattered around the disk. An install from
before 1.2.2 in `C:\BAMFApp` is migrated there automatically.

Then open http://localhost:8840

Full documentation: [`../README.md`](../README.md)
