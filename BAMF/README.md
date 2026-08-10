# BAMF — Basic ARP Monitoring Framework

A lightweight WatchYourLAN-style network monitor that runs on **Windows and
Linux** from the same codebase. Ping-sweeps your subnet, reads the ARP table,
tracks every MAC address it has ever seen in SQLite, flags unknown hosts, fires
a webhook when something new appears, and serves a web dashboard.

It installs as a Windows Service or a systemd unit (and runs fine from a
console on either), with the same database format, the same dashboard, and the
same features on both. Popular homes for it: a Windows Server box, or a Debian
LXC container on Proxmox.

## How it works

Every scan cycle (default 60 s) the service:

1. Pings every address in the subnet in parallel (populates the ARP cache).
2. Reads the system ARP table (`arp -a` on Windows, `/proc/net/arp` on Linux)
   and keeps complete/dynamic entries in the subnet.
3. Resolves hostnames via reverse DNS and vendors via OUI prefix lookup.
4. Upserts each host into `bamf.db`. A never-before-seen MAC is stored as
   **unknown** and triggers the webhook (if configured).
5. Hosts not seen this cycle are marked offline.

The dashboard at `http://<server>:8840` polls `/api/hosts` every 10 seconds.

## Requirements

- **Windows**: Server 2022 (also fine on Win 10/11 and Server 2019).
- **Linux**: any Debian-based distro (Debian 12 / Ubuntu 22.04+), bare metal,
  VM, or LXC container.
- .NET 8 SDK to build (https://dotnet.microsoft.com/download/dotnet/8.0)
  — the published output can be fully self-contained, so the *server* needs
  nothing installed if you publish that way.
- Optional, for active ARP scanning: Npcap on Windows, libpcap on Linux.

## Install

- **Windows** — extract the source anywhere and double-click
  `update\Install-BAMF.bat`. It elevates, builds to `C:\BAMFApp`, creates the
  `BAMF` service, and starts it; then open http://localhost:8840. Needs the
  .NET 8 SDK on that machine (it builds there). `update\Install-DesktopIcon.bat`
  adds a Desktop shortcut, and `update\Update-BAMF.bat` handles updates later —
  it's the same script, so installing and updating are one operation. Prefer to
  do it by hand? See [Install as a Windows Service](#install-as-a-windows-service).
- **Linux** — `linux/install.sh` handles install *and* updates end to end
  (dependencies, build to `/opt/bamf`, systemd unit, start). For Proxmox LXC
  specifics see `linux/README-PROXMOX.md`.

## Build

From this folder:

```powershell
# Framework-dependent (needs the .NET 8 runtime on the server):
dotnet publish -c Release -o publish

# OR fully self-contained single file (no runtime needed on the server):
dotnet publish -c Release -r win-x64   -p:PublishSingleFile=true --self-contained true -o publish   # Windows
dotnet publish -c Release -r linux-x64 -p:PublishSingleFile=true --self-contained true -o publish   # Linux
```

> **Rebuilding over a folder you already run from?** `dotnet publish` overwrites
> `appsettings.json` in the output folder — the build's copy always wins. Your
> `bamf.db` is safe (it isn't a build artifact), but your config is not. Copy it
> aside first and put it back after, or use the updaters below, which handle
> this for you.

## Run it manually first

```powershell
# Windows
cd publish; .\BAMF.exe
```

```bash
# Linux
cd publish && ./BAMF
```

Then open http://localhost:8840. Run it from a console at least once so you can
watch the log output and confirm the subnet detection and scan look right.

## Version

The version is declared once, as `<Version>` in `BAMF.csproj`, and shows up in
three places so you can always tell what's actually running:

- **Startup log** — `BAMF 1.2.0 (built 2026-08-10 00:30 UTC) starting`, the
  first line in the Windows Event Log or `journalctl -u bamf`.
- **`/api/hosts`** — `version` and `buildDate` fields alongside the scan metadata.
- **Dashboard header** — `v1.2.0 · 2026-08-10` next to the BAMF wordmark; hover
  for the full build timestamp.

Each build is also stamped with its UTC build date, because between releases
the version number doesn't change — the build date is what actually tells two
builds apart.

Handy after an update: Ctrl+F5 the dashboard and check the header actually
changed. If it didn't, the new build isn't the one running.

Bump `<Version>` on any merge that changes behaviour, not only at release time
— otherwise two meaningfully different builds both report the same number and
the only thing separating them is the build date. To cut a release, bump and
tag:

```bash
git tag v1.2.0 && git push --tags
```

## Configuration (`appsettings.json`)

| Setting | Meaning |
|---|---|
| `Urls` | Listen address. Default `http://0.0.0.0:8840` (all interfaces). |
| `Bamf:Subnets` | List of CIDRs to scan, e.g. `["192.168.1.0/24", "192.168.2.0/24"]`. Empty list = auto-detect every active IPv4 interface. (`Bamf:Subnet` as a single string still works for back-compat.) |
| `Bamf:HistoryRetentionDays` | Days of online/offline history to keep (default 90, pruned daily). |
| `Bamf:AutoDownloadOui` | Download the IEEE vendor registry on first run (default true). |
| `Bamf:ActiveArpScan` | Use raw ARP scanning via Npcap/libpcap when available; falls back to ping sweep otherwise. |
| `Bamf:ScanIntervalSeconds` | Seconds between scans. |
| `Bamf:AutoIgnoreRandomizedMacs` | Auto-ignore new hosts with randomized MACs (default in shipped config: true). |
| `Bamf:WebhookUrl` | Optional. POSTs when a new host appears. Discord webhook URLs get rich embeds automatically (amber alert cards with MAC/IP/vendor/network); other endpoints get generic JSON with a `content` field. Use the dashboard's Test webhook button to verify. |
| `Bamf:Password` | Optional. If set, the UI/API require it via HTTP Basic auth (any username). |
| `Bamf:DatabasePath` | SQLite file, relative to the exe. |

## Active ARP scanning (optional, recommended)

With `ActiveArpScan` enabled (default in shipped config), BAMF sends raw ARP
requests to every address instead of relying on ping - catching devices whose
firewalls drop ICMP. This needs a packet-capture driver:

- **Windows** — install the free **Npcap** from https://npcap.com (defaults are
  fine), then restart the BAMF service.
- **Linux** — install `libpcap` (`linux/install.sh` does this for you) and make
  sure the service can open raw sockets; the shipped systemd unit grants
  `CAP_NET_RAW`/`CAP_NET_ADMIN`.

There's also a toggle switch in the dashboard header - flip it anytime and the
new mode applies from the next scan cycle. The dashboard toggle is stored in
the database and overrides the `ActiveArpScan` value in appsettings.json.

The "Last scan" card shows which mode ran (`active ARP` or `ping sweep`), and
each network tab's tooltip shows its mode. If the capture driver is missing or
a raw scan fails, BAMF logs a warning and automatically falls back to ping
sweep - nothing breaks.

Windows note: if you chose "Restrict Npcap driver's access to Administrators only"
during install, the service account needs admin rights - either reinstall Npcap
without that option, or run the service as LocalSystem (recreate it without the
`obj=` argument).

## Vendor names

On first run BAMF downloads the full IEEE vendor registry automatically in the
background (set `AutoDownloadOui` to false to stay offline) and saves it as
`oui.csv` next to the exe. Until the download completes, a small built-in table
covers common vendors; full names fill in on the next scan. You can also place
`oui.csv` there manually (https://standards-oui.ieee.org/oui/oui.csv).

## Mobile

Under ~700px wide the device table reflows into a card layout — one card per
device with labeled fields — so it's usable one-handed on a phone without
horizontal scrolling. The who's-home board and everything else adapt too.

## Notes

Open a host's History panel to find a **Notes** field — free text for
remembering what a device is ("garage ESP32 sensor", "kids' iPad, bedtime 9pm").
A 📝 appears next to the name when a note exists (hover to preview), and notes
are included in search. Like custom names, notes are bound to the MAC and
survive IP changes.

## Fun extras

- **24h sparkline** - each row shows a tiny bar strip of the host's online/offline
  pattern over the last day, built from event history.
- **Who's home board** - a tab showing your watched devices as presence tiles
  (green = home/online, grey = away/offline). Great for people-devices.
- **17 themes** - click the theme button for a picker: Dark, Light, Terminal,
  Amber CRT, Synthwave, Commodore 64, Game Boy, Nord, Dracula, Solarized (dark
  + light), Gruvbox, High Contrast, Matrix, Blueprint, Hacker Red, Cotton Candy.
  CRT themes get scanlines; each pick triggers a BAMF! splat. Choice persists.

## Theme

A sun/moon button in the header toggles light and dark mode - and yes, every
flip triggers a 1960s-Batman-style comic splat (BAMF!, POW!, ZAP!...). The
choice persists across reloads. Respects prefers-reduced-motion.

## Desktop shortcut (Windows)

Run `update\Install-DesktopIcon.bat` once. It puts a **BAMF** shortcut on your
Desktop with the burst icon; double-clicking it opens the dashboard, silently
starting the service first if it isn't running (no admin prompt when the
service is already up). The launcher and icon are copied to `C:\BAMFApp`, so
the shortcut survives updates.

## What an update preserves

Both updaters are built so an update never costs you anything. Your settings
live in three places, and it helps to know which is which:

| What | Where it lives | On update |
|---|---|---|
| Subnets, password, webhook URL, scan interval, ping tuning | `appsettings.json` | Copied aside and restored. The version's fresh defaults are written next to it as `appsettings.new.json` so you can merge in any new options. |
| Custom names, notes, watch stars, ignored/known flags, all online-offline history, **and the dashboard's active-ARP and auto-ignore toggles** | `bamf.db` | Never touched, and snapshotted to `backups/` first (last 10 kept). |
| Theme choice | your browser's localStorage | Not on the server at all, so nothing can disturb it. |

The second row is the one people don't expect: the header toggles are stored in
the database, not the config file, and the database value **overrides**
`appsettings.json`. So if a toggle seems to ignore your config after an update,
that's why - flip it in the dashboard.

### Rolling back

The snapshots in `backups/` are ordinary SQLite files - restoring one is a copy:

```powershell
# Windows
Stop-Service BAMF
Copy-Item C:\BAMFApp\backups\bamf-20260808-231500.db C:\BAMFApp\bamf.db -Force
Start-Service BAMF
```

```bash
# Linux
systemctl stop bamf
cp /opt/bamf/backups/bamf-20260808-231500.db /opt/bamf/bamf.db
systemctl start bamf
```

To roll the *config* back instead, your previous `appsettings.json` is the one
still in place - it's `appsettings.new.json` that holds the incoming defaults.

## One-click updates (Windows)

On Linux, updating is the same one command as installing:
`bash /opt/bamf/install.sh /root/BAMF.zip` — it preserves your config and
database and snapshots the DB into `/opt/bamf/backups` first.

The `update` folder contains `Update-BAMF.bat` + `update.ps1`. One-time setup:
copy both files somewhere permanent (the Desktop is fine). The updater builds
from the zip via a temp folder, so the only folder BAMF keeps on disk is
`C:\BAMFApp` - app, config, database, and backups all live there. An old
`C:\BAMF` source folder from earlier versions is no longer used and can be
deleted.

From then on, updating is:

1. Download the new `BAMF.zip` (to Downloads).
2. Double-click `Update-BAMF.bat`. It elevates, stops the service, swaps the
   source, rebuilds, restores your `appsettings.json`, and restarts the service.
3. Ctrl+F5 the dashboard.

**Which source it builds** is decided in this order: an explicit `-ZipPath`, then
the source tree the script is sitting in, then the newest `BAMF*.zip` in
Downloads. So running `update.ps1` out of a freshly downloaded source tree uses
*that* tree — it won't reach past it for a stale zip left in Downloads months
ago. The script prints the source it chose and the version it's about to
install, e.g. `Installing version 1.0.0 (replacing 0.9.0)`.

The updater **follows the installed service**: it reads the service's binary
path and updates that folder, rather than assuming `C:\BAMFApp`. Older installs
often live elsewhere, and building into the default while the service still
pointed at the old folder used to look like a successful update that changed
nothing. It also verifies the build produced an exe and a `wwwroot`, prints the
version it installed and the path it's running from, and warns loudly if the
service still points somewhere else.

Your database and dashboard settings are never touched, and every update
snapshots `bamf.db` into `C:\BAMFApp\backups` first (last 10 kept). If an update ships new
config options, the fresh defaults are saved as `appsettings.new.json` next to
your kept config so you can merge anything interesting.

## Install as a Windows Service

Most people should just double-click `update\Install-BAMF.bat`, which does all
of this. The manual route, if you want control over the location or the service
account:

Copy the `publish` folder somewhere permanent, e.g. `C:\BAMF`, then in an
elevated PowerShell:

```powershell
sc.exe create BAMF binPath= "C:\BAMF\BAMF.exe" start= auto obj= "NT AUTHORITY\LocalService"
sc.exe description BAMF "LAN host discovery and monitoring"
sc.exe start BAMF
```

Notes:
- `LocalService` is a low-privilege account and is sufficient: ping, `arp -a`,
  and DNS all work without admin rights. If you hit permission issues writing
  the database, grant that account modify rights on `C:\BAMF`, or run as
  `LocalSystem` (remove the `obj=` argument).
- Logs go to the Windows Event Log (source: BAMF / .NET Runtime) when
  running as a service.

Open the firewall so other machines can reach the dashboard:

```powershell
New-NetFirewallRule -DisplayName "BAMF UI" -Direction Inbound -Protocol TCP -LocalPort 8840 -Action Allow
```

Uninstall:

```powershell
sc.exe stop BAMF
sc.exe delete BAMF
```

## Install as a systemd service (Linux)

`linux/install.sh` does the whole job as root — installs libpcap and the .NET 8
SDK (one-time), builds a self-contained binary to `/opt/bamf`, installs
`linux/bamf.service`, enables it, and starts it:

```bash
bash linux/install.sh              # from inside the source tree
bash linux/install.sh /root/BAMF.zip   # or straight from the zip
```

Notes:
- Everything lives in `/opt/bamf` — binary, `appsettings.json`, `bamf.db`, and
  `backups/`. Re-running the script updates in place and keeps all of it: your
  `appsettings.json` is restored afterwards (the version's fresh defaults are
  left as `appsettings.new.json`) and the database is snapshotted into
  `/opt/bamf/backups` first. See [What an update preserves](#what-an-update-preserves).
- If a `bamf.service` already exists pointing somewhere other than `/opt/bamf`,
  the script updates *that* folder and rewrites the unit to match, so an update
  can't silently install next to the copy you're actually running.
- The unit runs as `root` with `AmbientCapabilities=CAP_NET_RAW CAP_NET_ADMIN`
  so active ARP scanning works.
- Logs: `journalctl -u bamf -f`. Control: `systemctl {status,restart,stop} bamf`.
- If a firewall is in the way, open TCP 8840 (e.g. `ufw allow 8840/tcp`).

Uninstall:

```bash
systemctl disable --now bamf && rm /etc/systemd/system/bamf.service && systemctl daemon-reload && rm -rf /opt/bamf
```

## Using the API

Plain HTTP on the same port as the dashboard - no SDK, no tokens, no
negotiation. Everything below works from `curl`, a browser, a script, or
anything that speaks HTTP.

**Base URL**: `http://<server>:8840`

**Auth**: none unless you set `Bamf:Password`. If you have, every route needs
HTTP Basic auth with *any* username and that password:

```bash
curl -u x:yourpassword http://192.168.2.137:8840/api/hosts
```

**Reading.** `GET /api/hosts` is the full JSON picture - devices plus scan
metadata (`version`, `buildDate`, `subnets`, `lastScan`, per-network scan
modes). Each device carries an `id`, which is what the per-host routes take:

```bash
curl http://192.168.2.137:8840/api/hosts
curl http://192.168.2.137:8840/api/hosts.txt      # same devices, plain text
curl http://192.168.2.137:8840/api/events         # recent online/offline events
```

**Changing things.** POST with a JSON body:

```bash
# name a device (empty string clears it back to the DNS name)
curl -X POST http://192.168.2.137:8840/api/hosts/16/name \
  -H "Content-Type: application/json" -d '{"name":"Kevin PC"}'

# approve it, watch it for downtime, hide it
curl -X POST http://192.168.2.137:8840/api/hosts/16/known   -H "Content-Type: application/json" -d '{"known":true}'
curl -X POST http://192.168.2.137:8840/api/hosts/16/watch   -H "Content-Type: application/json" -d '{"watched":true}'
curl -X POST http://192.168.2.137:8840/api/hosts/16/ignore  -H "Content-Type: application/json" -d '{"ignored":true}'

# no body needed
curl -X POST http://192.168.2.137:8840/api/hosts/16/wake
curl -X POST http://192.168.2.137:8840/api/hosts/16/identify
```

**Scanning** is `GET` - it returns results rather than changing state:

```bash
curl "http://192.168.2.137:8840/api/hosts/16/portscan?ports=22,80,443"
curl "http://192.168.2.137:8840/api/portscan/ip?ip=192.168.2.50"
curl "http://192.168.2.137:8840/api/portscan/pattern?ip=*.245"
curl "http://192.168.2.137:8840/api/portscan?subnet=192.168.2.0/24"
```

### Handing the network to a script or an AI agent

Point it at `GET /api/hosts.txt` and nothing else. One request returns a
complete, current picture; the output is sorted by network then numeric IP, so
two fetches diff cleanly; and **every route that changes anything is a POST or
a DELETE**, so a consumer restricted to that one GET cannot rename, wake,
scan, or delete a thing.

## API

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/hosts` | All hosts + scan metadata (includes the running `version`) |
| GET | `/api/hosts.txt` | The same devices as a plain-text fixed-width table — no JSON, no markup. For `curl`, a terminal, or pointing a read-only agent at |
| POST | `/api/hosts/{id}/identify` | On-demand device fingerprint: one ICMP echo for the TTL plus a short fingerprint-port probe. Returns the guess, TTL, and open ports, and saves the guess |
| POST | `/api/hosts/{id}/known` | Body `{"known": true}` — approve/unapprove a host |
| POST | `/api/webhook/test` | Send a test notification to `Bamf:WebhookUrl` (the dashboard's Test webhook button). Returns `{ok:true}` or `{ok:false,error:"…"}` |
| POST | `/api/settings/active-arp` | Body `{"enabled": true}` — toggle active ARP scanning at runtime |
| POST | `/api/settings/auto-ignore-random` | Body `{"enabled": true}` — toggle auto-ignoring of randomized MACs at runtime |
| POST | `/api/hosts/{id}/watch` | Body `{"watched": true}` — watch a host for downtime (star toggle in the UI) |
| POST | `/api/hosts/{id}/ignore` | Body `{"ignored": true}` — hide a host from main views and suppress its alerts/history |
| POST | `/api/hosts/{id}/note` | Body `{"note": "..."}` — save a free-text note (max 500 chars) |
| POST | `/api/hosts/{id}/name` | Body `{"name": "Kevin's PC"}` — set a friendly name (empty string clears it). In the UI, click a host's name to edit it. |
| POST | `/api/hosts/{id}/wake` | Send a Wake-on-LAN magic packet to the host (button appears on offline hosts) |
| GET | `/api/hosts/{id}/portscan` | On-demand port check for one host. Optional `?ports=22,80,8000-8100` for a custom set (default: ~18 common ports) |
| GET | `/api/portscan` | On-demand port scan across online hosts. Optional `?ports=...` and `?subnet=...` |
| GET | `/api/portscan/ip` | On-demand port check for any IP: `?ip=192.168.1.50`, optional `?ports=...`. The address need not be a known host; private ranges only (400 otherwise) |
| GET | `/api/portscan/pattern` | Wildcard scan: `?ip=*.245`, optional `?ports=...`. Expands only across configured subnets, capped at 256 addresses |
| GET | `/api/events` | Network-wide activity feed (recent online/offline events, all hosts) |
| GET | `/api/hosts/{id}/events` | One host's online/offline event history |
| POST | `/api/hosts/{id}/forget` | Body `{"forgotten": true}` — soft-delete to the Forgotten tab (reversible) |
| DELETE | `/api/hosts/{id}` | Permanently delete a host and its history (from the Forgotten tab) |

## Device links and port check

Each host's IP is a link that opens `http://<ip>` in a new tab - handy for
routers, NAS boxes, printers, and cameras with web UIs. The **Ports** button
runs an on-demand check of ~18 common service ports (HTTP, HTTPS, SSH, SMB,
RDP, print, Plex, etc.) for that one host and shows what's open; web ports
become clickable links with the right scheme.

Four ways to scan:
- **Per host, common ports** - click **Ports** on a host row.
- **Per host, custom ports** - **Shift+click** Ports and enter a spec like
  `22,80,443,8000-8100`.
- **Network-wide** - the **Scan ports** button above the table opens a dialog:
  choose all online hosts or one network, common or custom ports.
- **One specific IP** - in that same dialog pick **Specific IP address…** and
  type an address. It doesn't have to be a host BAMF knows about, which makes
  it useful for something that never answered a scan, a device you just plugged
  in, or an address on a subnet you aren't monitoring. If the address does
  match a known host, the result is labeled with that host's name.
- **A wildcard pattern** - the same field accepts `*` and `?`. `*.245` scans
  that address on every configured network, `192.168.2.*` walks a subnet, and
  `192.168.2.1?` covers `.10`-`.19`. Patterns only expand across networks in
  your `Subnets` config, are capped at 256 addresses (so `*.*` is refused
  rather than becoming a sweep), and the result shows how many addresses were
  scanned.

  Only private addresses can be scanned this way (RFC1918, loopback,
  link-local, CGNAT). The dashboard can be exposed without a password, and this
  keeps it from being used as an internet port scanner.

All on-demand, never automatic. Single ranges are capped at 2000 ports and
host concurrency is bounded, so a scan can't turn into a network flood.

Results open in a **dockable panel** on the right that stays until you close
it, and keeps a log of every scan you run (newest on top) so you can compare
hosts. Web ports are clickable links. The panel is never wiped by auto-refresh.

## Device identification

Each device gets a best-effort guess at what it actually is, shown in small
text under the vendor in its row. There are two tiers, kept separate so nothing
probes your network unless you ask it to.

### Tier 1 — passive, automatic, zero packets

After every scan cycle BAMF fills in guesses for devices that don't have one,
using only data it already holds: the vendor (from the MAC's OUI) and the
hostname (from reverse DNS or NetBIOS).

| Signal | Guess |
|---|---|
| `Nest Labs`, `Google` | Google/Nest device |
| `HP Inc.`, `Canon`, `Epson`, `Brother` | Printer or HP device |
| `Espressif` | ESP32/ESP8266 IoT |
| `Raspberry Pi` | Raspberry Pi |
| `Ubiquiti`, `MikroTik`, `TP-Link`, `Netgear`, `Cisco`, `Aruba` | Network gear |
| `Synology`, `QNAP` | NAS |
| `Apple` | Apple device |
| `Amazon` | Amazon device |
| `Vizio`, `Roku`, `Samsung Electronics`, `LG Electronics` | TV or media device |
| hostname contains `iphone` / `ipad` / `android` | that device |

**No packets are sent for this**, so the never-automatic-scanning rule holds.
Devices that already have a guess are skipped, so repeat scans cost nothing. On
a typical home network this identifies well over half the devices for free.

### Tier 2 — active, on demand only

**Identify device** in a host's ⋯ menu (or `POST /api/hosts/{id}/identify`)
does the real work:

**1. One ICMP echo, to read the reply's TTL.** Each OS starts packets at a
characteristic TTL, and a device on your own LAN is 0–1 hops away, so what
arrives is essentially what it sent:

| TTL observed | Suggests |
|---|---|
| ≤ 64 | Linux, Unix, Android, iOS, most IoT |
| 65–128 | Windows |
| > 128 | Network gear, BSD, some printers |

**2. A probe of eleven telling ports** — chosen for what they reveal rather
than for coverage: 445 and 139 (SMB), 3389 (RDP), 22 (SSH), 9100 and 631
(print), 62078 (iOS lockdown), 32400 (Plex), 80, 443, and 5900 (VNC).

**3. Combine, with ports overriding the TTL** where they're more specific: port
62078 means iOS whatever the TTL suggested, 9100 means printer, 3389 means
Windows.

The result always names its own evidence, so you can judge it yourself rather
than take it on faith:

```
Windows (TTL 128, SMB)
Linux/Unix (incl. Android, iOS, most IoT) (TTL 64)
Printer or HP device (vendor)          ← answered nothing; fell back to tier 1
```

Results appear in the scan panel alongside port scans, and the guess is saved.

### Where the guess shows up

Under the vendor in the device row, in `/api/hosts` as `osGuess`, in the
`DEVICE GUESS` column of `/api/hosts.txt`, and in the CSV export. It's also
**searchable** — `*Windows*` in the search box finds every device fingerprinted
as Windows, which is a quick way to audit what's on the network.

### Limits worth knowing

- **It's a heuristic, not nmap.** No TCP/IP stack fingerprinting, no service
  banner parsing, no version detection.
- **A silent device stays unidentified.** One that answers neither ICMP nor any
  probed port keeps whatever the vendor suggested — which is often still right,
  but no better than free.
- **TTL can be changed.** It's a normal OS setting and some appliances alter
  it, so treat it as evidence rather than proof.
- **Vendor matching is substring-based**, so an unusual OUI spelling simply
  won't match instead of producing a wrong answer.

The bias throughout is to report "Unknown" rather than invent something
confident and false.

## Search

The search box does substring matching across name, hostname, IP, MAC, vendor,
note, and device guess. Add `*` or `?` and it becomes a wildcard match against
each field on its own:

| Query | Finds |
|---|---|
| `*.245` | any IP ending in `.245`, on every network |
| `192.168.2.*` | everything on that subnet |
| `192.168.2.1?` | `.10` through `.19` |
| `*Windows*` | everything fingerprinted as Windows |
| `HP*` | vendors starting with HP |

The same wildcards work in the port scan dialog's **Specific IP address…**
field, where they expand into addresses to probe rather than filtering the
list — see [Device links and port check](#device-links-and-port-check).

## Plain-text device list

`GET /api/hosts.txt` returns the whole device table as fixed-width plain text —
no JSON, no buttons, nothing to parse around:

```bash
curl http://<server>:8840/api/hosts.txt
```

Rows are sorted by network then numeric IP, so two fetches diff cleanly. Handy
in a terminal, and a tidy read-only way to hand an AI agent an accurate picture
of the network. It respects `Bamf:Password` like every other route.

## Down alerts (watch)

Click the ☆ **star** on any host to watch it. Watched hosts fire a Discord
alert when they go offline (red card) and again when they recover (green card,
with how long they were down). Ideal for a NAS, cameras, or a server you want
to know about the moment they drop. Requires `WebhookUrl` to be set. Watching
is stored per host (survives restarts) and is independent of known/ignored.

## Wake-on-LAN

Offline hosts get a **Wake** button that broadcasts a WoL magic packet (to the
global broadcast and the device's subnet directed-broadcast). For it to work:
the target must have Wake-on-LAN enabled in its BIOS/UEFI and OS network
adapter settings, and the BAMF server must have an interface on the target's
subnet (magic packets are layer-2 broadcasts and don't route). Give a woken
device a minute to boot; it'll flip to online on the next scan.

## Multi-network setups

BAMF discovers hosts via ARP, which is layer-2 and does not route between
subnets. To watch multiple networks the server needs an interface on each one
(e.g. one `netX` per subnet on a Proxmox LXC). BAMF binds its discovery probes
to the correct local interface per subnet, so a multi-homed host scans every
network on every cycle. If a subnet in your config has no matching local
interface, the log warns you and that subnet's hosts will appear offline.

## Limitations to be aware of

- ARP only sees the L2 segment the server sits on. Hosts on other VLANs are
  invisible unless the server has an interface on that segment and it's listed
  in `Subnets`. The dashboard's network switcher shows each configured network
  separately.
- Hostnames come from reverse DNS first, then a NetBIOS query (UDP 137) as a
  fallback - this names many Windows PCs, NAS boxes, and printers that have no
  DNS record. Devices that answer neither (lots of IoT gear, phones, some smart
  TVs) still show "-"; name those by hand, and the name sticks to the MAC.
- Phones with MAC randomization appear as new "(randomized MAC)" hosts each
  time they rejoin. With auto-ignore enabled (default; toggle in the
  dashboard header or via `AutoIgnoreRandomizedMacs`), these are auto-filed
  under the Ignored tab and never alert. You can also manually
  Ignore/Unignore any host from the dashboard.
- In ping-sweep mode, hosts that neither answer ping nor talk on the network
  during a cycle may briefly show offline even if powered on. Enable
  `ActiveArpScan` + install Npcap to catch these.
