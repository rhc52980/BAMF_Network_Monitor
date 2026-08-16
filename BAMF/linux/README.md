# BAMF on Linux — start here

Everything in this folder is for Linux. There is one script, and it both
installs and updates:

```bash
sudo bash install.sh                 # from inside this source tree
sudo bash install.sh /root/BAMF.zip  # or straight from a package
```

It installs the dependencies (libpcap, and the .NET 8 SDK on first run), builds
to `/opt/bamf`, installs the systemd unit, and starts the service. Re-running it
later is the update path — your `appsettings.json` and `bamf.db` are preserved,
and the database is snapshotted to `/opt/bamf/backups` first.

| File | What it is |
|---|---|
| **`install.sh`** | Install **and** update. The only thing you run. |
| `bamf.service` | The systemd unit it installs. |
| `README-PROXMOX.md` | Container setup, including networking for multiple subnets. |

## Where things end up

Everything lives in **`/opt/bamf`** — the binary, `appsettings.json`, `bamf.db`,
`backups/`, and a copy of `install.sh` for future updates.

```bash
systemctl status bamf      # is it running
journalctl -u bamf -f      # what is it doing
```

Then open `http://<server>:8840`

Full documentation: [`../README.md`](../README.md)
