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

## Where everything lives

Two folders in the source, one per platform — open the one for your OS and its
`README.md` tells you which file to run:

| | Windows | Linux |
|---|---|---|
| **Scripts** | `BAMF\windows\` | `BAMF/linux/` |
| **What you run** | `Install-BAMF.bat` (first time)<br>`Update-BAMF.bat` (later) | `install.sh` (both) |
| **Installs to** | `C:\BAMF` | `/opt/bamf` |
| **Your config** | `C:\BAMF\appsettings.json` | `/opt/bamf/appsettings.json` |
| **Database** | `C:\BAMF\bamf.db` | `/opt/bamf/bamf.db` |
| **DB snapshots** | `C:\BAMF\backups\` | `/opt/bamf/backups/` |

Everything an install owns is in that one folder, so backing BAMF up means
copying it, and removing BAMF means deleting it plus the service.

## Install

- **Windows** — extract the source anywhere and double-click
  `windows\Install-BAMF.bat`. It elevates, builds to `C:\BAMF`, creates the
  `BAMF` service, and starts it; then open http://localhost:8840. Needs the
  .NET 8 SDK on that machine (it builds there). `windows\Install-DesktopIcon.bat`
  adds a Desktop shortcut, and `windows\Update-BAMF.bat` handles updates later —
  it's the same script, so installing and updating are one operation. Prefer to
  do it by hand? See [Install as a Windows Service](#install-as-a-windows-service).
- **Linux** — `linux/install.sh` handles install *and* updates end to end
  (dependencies, build to `/opt/bamf`, systemd unit, start). For Proxmox LXC
  specifics see `linux/README-PROXMOX.md`.
- **Docker** — `docker run` the image from GHCR on the host's network; nothing
  to build. See [Docker](#docker).

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

- **Startup log** — `BAMF 1.9.0 (built 2026-08-25 21:40 UTC) starting`, the
  first line in the Windows Event Log or `journalctl -u bamf`.
- **`/api/hosts`** — `version` and `buildDate` fields alongside the scan metadata.
- **Dashboard header** — `v1.9.0 · 2026-08-25` next to the BAMF wordmark; hover
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
git tag v1.9.0 && git push --tags
```

## Configuration (`appsettings.json`)

| Setting | Meaning |
|---|---|
| `Urls` | Listen address. Default `http://0.0.0.0:8840` (all interfaces). |
| `Bamf:Subnets` | List of CIDRs to scan, e.g. `["192.168.1.0/24", "192.168.2.0/24"]`. Empty list = auto-detect every active IPv4 interface. (`Bamf:Subnet` as a single string still works for back-compat.) |
| `Bamf:DeviceLinkTemplate` | Where a device's IP link points when it has no link of its own. `{ip}` is the device address. Default `http://{ip}`. |
| `Bamf:HistoryRetentionDays` | Days of online/offline history to keep (default 90, pruned daily). |
| `Bamf:ThemesPath` | Folder for drop-in themes, relative to the exe (default `themes`). See [Drop-in themes](#drop-in-themes). |
| `Bamf:AutoDownloadOui` | Download the IEEE vendor registry on first run (default true). |
| `Bamf:UpdateCheck` | Check GitHub daily for a newer release and show a badge (default **false**). Read-only — never downloads or installs. Header toggle overrides this. |
| `Bamf:UpdateRepo` | Repository the update check reads. Change it if you run a fork. |
| `Bamf:ActiveArpScan` | Use raw ARP scanning via Npcap/libpcap when available; falls back to ping sweep otherwise. |
| `Bamf:ScanIntervalSeconds` | Seconds between scans. |
| `Bamf:AutoIgnoreRandomizedMacs` | Auto-ignore new hosts with randomized MACs (default in shipped config: true). |
| `Bamf:HookToken` | A token for the inbound webhooks, sent as `X-BAMF-Token` or `?token=`. With a `Password` set, it stands in for the password on those endpoints; without one, they're as open as the dashboard (default empty). |
| `Bamf:Remotes` | Other BAMF servers to show here, read-only: `[{"Name": "Cabin", "Url": "http://10.0.0.5:8840", "Password": ""}]`. See [Other BAMF servers](#other-bamf-servers). |
| `Bamf:Mqtt:*` | Presence per device over MQTT: `Server` (set it to turn this on), `Port` (1883), `Tls`, `Username`, `Password`, `ClientId` (bamf), `TopicPrefix` (bamf), `Discovery` (true), `DiscoveryPrefix` (homeassistant). Read at startup only. See [MQTT](#mqtt-and-home-assistant). |
| `Bamf:TrafficMonitor` | With Npcap, watch the wire receive-only: bytes in and out per device, and every DHCP and DNS server in use, alerting on new ones (default true). Also in Settings → Behaviour. See [Traffic, DHCP and DNS](#traffic-dhcp-and-dns). |
| `Bamf:LatencyProbe` | After each scan, ping every online device on the networks it covered and keep the round-trip time (default true). Also in Settings → Behaviour, which wins once changed there. See [Latency and uptime](#latency-and-uptime). |
| `Bamf:HolidaySpirit` | `true` puts every dashboard in the Halloween theme from October 1st to 31st and the Christmas theme from December 1st to 25th (default false). Also in Settings → Behaviour, which wins once changed there. See [Holiday Spirit](#holiday-spirit). |
| `Bamf:WebhookUrl` | Optional starting value for the notification webhook — the dashboard's **Tools → Notifications** saves over it. POSTs when a new host appears. Discord webhook URLs get rich embeds automatically (amber alert cards with MAC/IP/vendor/network); other endpoints get generic JSON with a `content` field. Use the dashboard's Test webhook button to verify. |
| `Bamf:Password` | Optional. If set, the UI/API require it via HTTP Basic auth (any username). Over plain HTTP the credential is only base64-encoded — see [What BAMF talks to](#what-bamf-talks-to). |
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

There's also a toggle in the Settings tab (**Behaviour → Active ARP
scanning**) - flip it anytime and the new mode applies from the next scan
cycle. The dashboard toggle is stored in the database and overrides the
`ActiveArpScan` value in appsettings.json.

The "Last scan" card shows which mode ran (`active ARP` or `ping sweep`), and
each network tab's tooltip shows its mode. If the capture driver is missing or
a raw scan fails, BAMF logs a warning and automatically falls back to ping
sweep - nothing breaks.

Windows note: if you chose "Restrict Npcap driver's access to Administrators only"
during install, the service account needs admin rights - either reinstall Npcap
without that option, or run the service as LocalSystem (recreate it without the
`obj=` argument).

## Update check (optional, off by default)

BAMF can check GitHub once a day for a newer release and show an **update
1.4.0** badge in the header, linking to the release page.

It is **off by default and opt-in**, because BAMF often runs on isolated
networks and this is the only outbound call it would make that you didn't ask
for. Turn it on in the Settings tab (**Behaviour → Check GitHub daily for a
newer release**), or with `Bamf:UpdateCheck` in `appsettings.json` — the
dashboard value is stored in the database and overrides the config value, same
as the other settings in that tab.

What it does and doesn't do:

- Reads `https://api.github.com/repos/<repo>/releases/latest` at most once every
  24 hours, plus immediately when you switch it on.
- Compares the release tag with the running version and shows a badge if it's
  newer. **Nothing is downloaded and nothing is installed** — updating stays a
  deliberate act.
- Fails silently. No internet, GitHub down, rate limited, private repo: it logs
  at debug level and scanning carries on untouched.
- `Bamf:UpdateRepo` points it somewhere else if you run a fork.

The state is in `/api/hosts` under `update`, so scripts can see it too:

```json
"update": { "enabled": true, "available": true, "latest": "1.4.0",
            "url": "https://github.com/.../releases/tag/v1.4.0",
            "checkedUtc": "2026-08-11T03:46:00Z" }
```

Note: a **private** repository returns 404 to an unauthenticated request, so
the check quietly finds nothing until the repository is public.

## What BAMF talks to

Everything BAMF initiates on its own, and how it's protected:

| Connection | Protection | When |
|---|---|---|
| IEEE vendor registry (`standards-oui.ieee.org`) | **HTTPS** | First run, unless `AutoDownloadOui` is false |
| GitHub update check (`api.github.com`) | **HTTPS** | Daily, only if you enable the update check |
| Your webhook | **whatever scheme your URL uses** | When a new host appears, or a watched host changes state |
| Scanning: ARP, ICMP, UDP probes, NetBIOS (137), Wake-on-LAN (9), port checks | none — these protocols have none | Every scan / on demand |

Certificate validation is left at the .NET default: a bad or expired
certificate fails the request. Nothing in BAMF disables it.

The dashboard itself has **no external dependencies** — fonts are served
locally, and there are no CDN scripts, analytics, or tracking of any kind.

Two things worth knowing:

- **An `http://` webhook sends device names, MACs and IPs in plaintext.** BAMF
  logs a warning at startup if yours is one. Discord and most services offer
  HTTPS endpoints - use them.
- **HTTP Basic auth over plain HTTP is encoding, not encryption.** If you set
  `Bamf:Password`, the credential travels base64-encoded and anyone who can see
  traffic on that segment can read it. BAMF warns about this too. Fine on a
  trusted LAN; see below if not.

### Serving the dashboard over HTTPS

Point `Urls` at an `https://` address and give Kestrel a certificate. With a
PFX, in `appsettings.json`:

```json
{
  "Urls": "https://0.0.0.0:8843",
  "Kestrel": {
    "Certificates": {
      "Default": { "Path": "bamf.pfx", "Password": "your-pfx-password" }
    }
  }
}
```

A self-signed certificate is enough to encrypt the connection (browsers will
warn once, since nothing vouches for it):

```powershell
# Windows - creates the cert and exports it next to the exe
$c = New-SelfSignedCertificate -DnsName "bamf.lan","192.168.1.10" -CertStoreLocation Cert:\LocalMachine\My
$p = ConvertTo-SecureString "your-pfx-password" -AsPlainText -Force
Export-PfxCertificate -Cert $c -FilePath C:\BAMF\bamf.pfx -Password $p
```

```bash
# Linux
openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
  -subj "/CN=bamf.lan" -keyout /tmp/k.pem -out /tmp/c.pem
openssl pkcs12 -export -out /opt/bamf/bamf.pfx -inkey /tmp/k.pem -in /tmp/c.pem \
  -passout pass:your-pfx-password
```

Then restart the service and use `https://<server>:8843`. Remember to open the
new port and, if you had one, close the old 8840 rule.

## Vendor names

On first run BAMF downloads the full IEEE vendor registry automatically in the
background (set `AutoDownloadOui` to false to stay offline) and saves it as
`oui.csv` next to the exe. Until the download completes, a small built-in table
covers common vendors; full names fill in on the next scan. You can also place
`oui.csv` there manually (https://standards-oui.ieee.org/oui/oui.csv).

## Mobile

Under ~700px wide the device table reflows into a card layout — one card per
device with labeled fields — so it's usable one-handed on a phone without
horizontal scrolling. The who's-home board and everything else adapt too:

- A device's **⋯** menu and its "+N" address list open as a sheet from the
  bottom of the screen, where a thumb already is, with a dimmed page behind it.
  Tap outside the sheet to close it.
- Dialogs (add a switch, plug into, scan ports, and the rest) are full width
  and rise from the bottom, with their fields stacked under their labels.
- The four stat cards sit two by two, so the device list starts on the first
  screen. Activity rows put the name and what happened on one line and the
  address, network and time underneath.
- Buttons in the cards and the map tools are finger sized, and the page
  respects the phone's safe areas (notch, home bar).

Add the page to your home screen for a full-screen, app-like BAMF. A link with
a `#tab` in it opens straight on that tab — see [Linking to a tab](#linking-to-a-tab).

## Notes

Each device row has a **History** button opening a panel with its online/offline
session timeline. If the device has ever changed address, the panel also lists
every IP it has held and when each period began and ended — the current one
first — which is what you want when a DHCP pool is running dry or two devices
are fighting over a static address. Devices that have only ever had one address
show no list; a "same address since first seen" line would just be noise.
History before this feature existed is not reconstructed: a device that
predates it starts with the address it had at the time, dated from first seen.
Notes are separate: **Add note…** / **Edit note…** in the ⋯
menu opens a small dialog with free text for remembering what a device is
("garage ESP32 sensor", "kids' iPad, bedtime 9pm"). Ctrl+Enter saves, Escape
cancels, and **Clear** removes the note.

A 📝 appears next to the name when a note exists (hover to preview), and notes
are included in search. Like custom names, notes are bound to the MAC and
survive IP changes.

## Fun extras

- **24h sparkline** - each row shows a tiny bar strip of the host's online/offline
  pattern over the last day, built from event history.
- **Who's home board** - a tab showing your watched devices as presence tiles
  (green = home/online, grey = away/offline). Great for people-devices. Pick a
  tag instead to show one group of devices at a time.
- **Latency and uptime** - a Latency column with each device's round-trip time,
  a 24-hour latency chart in its History panel, and its uptime over the last 7
  and 30 days. See [Latency and uptime](#latency-and-uptime).
- **New tab** - every device first seen in the last 7 days, so a weekly check
  takes ten seconds.
- **Bandwidth per device** - with Npcap, bytes in and out per device: a Traffic
  column and a Top talkers card. See [Traffic, DHCP and DNS](#traffic-dhcp-and-dns).
- **DHCP and DNS watch** - an alert when a second DHCP server appears, or a
  device starts asking a DNS server it never used before.
- **Alert rules and quiet hours** - "kids' devices online after 10 pm", "the
  NAS offline for more than 15 minutes"; and quiet hours that hold every alert
  for one summary in the morning. See [Alert rules](#alert-rules-and-quiet-hours).
- **Port history and change alerts** - every port ever found open on a device
  is remembered, and a port that opens later is an alert. Optionally, a daily
  scan of every known device. See [Port history](#port-history-and-change-alerts).
- **A timeline per device** - first seen, every online and offline, each
  address move, ports opening and closing, and every alert that named it, in
  one list in the History panel.
- **Prometheus metrics** at `/metrics` - devices, presence, latency, uptime
  and traffic, for Grafana. See [Prometheus](#prometheus-metrics).
- **Inbound webhooks** - `POST /api/hooks/scan` and `/api/hooks/wake/{mac}`,
  so Home Assistant or a script can ask for a scan or wake a machine. See
  [Inbound webhooks](#inbound-webhooks).
- **Other BAMF servers** - watch another site's BAMF, read-only, under its own
  network tabs. See [Other BAMF servers](#other-bamf-servers).
- **Bandwidth history** - bytes per device per hour, kept for the retention
  window, so the History panel shows the week and reports say who used the most.
- **Scheduled reports** - a daily or weekly summary to your webhook: what's
  new, what went away, the flakiest, the longest offline. See
  [Scheduled reports](#scheduled-reports).
- **Export and import the layout** - everything you recorded as one JSON file,
  keyed by MAC, for backup or a move to a new server. See
  [Export and import the layout](#export-and-import-the-layout).
- **Home Assistant** - presence per device over MQTT, with discovery, so
  automations can fire when someone gets home. See [MQTT](#mqtt-and-home-assistant).
- **Tags** - group devices as "kids", "IoT", "work" or whatever fits, then
  filter the list and the Map by tag. See [Tags](#tags).
- **26 themes** - click the theme button for a picker: Dark, Light, Terminal,
  Amber CRT, Synthwave, Commodore 64, Game Boy, Nord, Dracula, Solarized (dark
  + light), Gruvbox, High Contrast, Matrix, Blueprint, Hacker Red, Cotton Candy,
  Thunderstorm, Hotdog Stand, Steampunk, Waterworks, Aquarium, Goat, Goat Night,
  Night Street and Constellation. See [Theme](#theme).
  CRT themes get scanlines; each pick triggers a BAMF! splat. Choice persists.
  There are also two seasonal themes you won't find in the list. They unlock the
  way a friendly program would: just tell it its name. (On a phone, the logo is
  listening.) With reduced motion turned on in your system settings, their
  decorations stay still.

## Fonts and offline use

The dashboard's fonts — Space Grotesk and IBM Plex Mono — are **served from the
BAMF machine**, not a CDN. `wwwroot/fonts.css` plus `wwwroot/fonts/*.woff2`
(Latin subset, ~110 KB total) ship with the app.

That matters for where BAMF actually runs: monitoring boxes frequently sit on
segments with no internet, and a CDN font link would leave the dashboard
rendering in fallback fonts there. It also means loading the dashboard never
reports the viewer's IP to a third party — worth knowing if you run this
somewhere with privacy obligations.

Both fonts are licensed under the **SIL Open Font License 1.1**, which permits
redistribution, including commercially. To refresh or add scripts beyond Latin,
regenerate the files from Google Fonts and update `fonts.css`.

The dashboard has no other external dependencies: no CDN scripts, no analytics,
no outbound calls at all. The only network traffic BAMF originates is scanning
your subnets and, if configured, the vendor-registry download and your webhook.

## Theme

The ◑ button in the header opens the theme menu: Dark, Light, Terminal, Amber
CRT, Synthwave, Commodore 64, Game Boy, Nord, Dracula, Solarized, Solar Light,
Gruvbox, High Contrast, Matrix, Blueprint, Hacker Red, Cotton Candy,
Thunderstorm, Hotdog Stand, Steampunk, Waterworks, Aquarium, Goat, Goat Night,
Night Street, Constellation and Claw Machine. Your choice is remembered in
your browser.

Some themes have a little life in them:

- **Matrix**: digital rain falls behind the page, faint behind the panels so
  everything stays readable. Now and then a column spells out one of your
  devices' names or addresses, and a new device drops its name down the screen
  in white. The numbers decode when they change, and a row glitches when its
  device goes offline or changes address. On the Map, data flows along the
  cables you've recorded. Switching to Matrix plays a short intro, and every
  so often a white rabbit hops along the bottom. Follow it.
- **Terminal**: Battlezone. The green vector world of the 1980 arcade tank
  game lies behind the page, dimmed so the page stays readable: a ring of
  mountains with the volcano spitting lava and the crescent moon, turning
  slowly as you look around. Wireframe tanks prowl the plain, one for each
  unknown device (up to four), and a saucer drifts over now and then. A radar
  sweeps in the header with your score beside it. A finished scan fires a shell
  at the nearest tank, and a hit scores 1000. A new device flashes **ENEMY IN
  RANGE** and brings a tank in. A device going offline cracks the glass, as
  taking a hit did in the arcade. Also a blinking block cursor.
- **Synthwave**: a neon grid rolling toward a striped sunset.
- **Amber CRT**: rounded glass at the edges, and a gentle phosphor flicker.
- **Blueprint**: drawn on grid paper.
- **Hacker Red**: the header glitches now and then.
- **Cotton Candy**: a few bubbles drift up.
- **Waterworks**: pipes, valves and water. A steel main runs under the
  header with water shimmering through it, and steel pipes line the sides.
  The counts sit in water-meter windows that roll when they change, and the
  next-scan bar is a glass sight tube filling with water. A tank in the header
  shows how much of your network is up, against the most seen online this
  session, with a ripple on top when everything's on. Water pulses along the
  Map's pipes to each device and stands still to one that's offline, and the
  Map fills from the bottom when you open it. A finished scan surges the
  water. A new device spins a valve open and hangs a name tag. A device going
  offline springs a leak on its row, and watched devices carry a red shut-off
  valve that closes when they drop. Unknown devices get an inspection tag.
  Rows ripple when you point at them, the search box is a drain port, and
  rows get a wash as you type. With sound on (the 🔊 button beside the theme
  button, off until you click it) the drips plop, a new device's valve
  squeaks open, a device going offline leaks with a patter of drops, and a
  finished scan surges through the mains. A pipe bursting cracks and then
  hisses, and a relief valve lets off with a pssht. Behind the page, a whole
  plant: glass-lined mains with water running through them, up both sides,
  along the floor, across the top and through a manifold on the right, with
  tees at every junction and flanges along every run. Valves of every kind
  sit along the pipes: red gate wheels, ball valves with a lever, a yellow
  butterfly valve, a check valve and spring-loaded relief valves. Four
  pressure gauges read how much of your network is up, a pump hums on the
  floor main, and open pipe ends pour into the reservoir along the bottom.
  A few joints drip all the time and a seal mists. A scan turns the valve
  wheels, spikes the gauges and lifts every relief valve. Every half a minute
  or so a pipe bursts under high pressure: water jets out of the split, the
  pipes shudder, the nearest gauge slams into the red, and the nearest wheel
  spins shut on it. The main under the header carries valves of its own, and
  now and then it bursts too, spraying down over the page. A device going
  offline bursts a pipe, shuts a ball valve for a moment and spurts water
  out of its row. On the Map, the thin "on this network" lines are copper
  pipe.
- **Aquarium**: fish, bubbles, swaying weed and gravel behind the glass, with
  light rays drifting through, and now and then a shark cruising past. A new device swims in as a fish carrying its
  name, a finished scan sends up a column of bubbles, and a device going
  offline sinks for a moment. Watched devices have a pet fish that swims
  beside their name and floats still when they're down. On the Map, bubbles
  drift up the pipes and a shell marks unknown devices. With sound on, the
  bubbles burble as they rise, a new device arrives in a flurry of them, and
  the pump hums quietly in the background; it stops in a background tab.
- **Goat**: an alpine pasture behind the page, with snowy mountains, pines,
  drifting clouds and a goat keeping watch from a crag, under a barn-red
  header. A herd lives along the meadow at the bottom of the page, and every
  goat has a mind of its own: it wanders, grazes, pronks and bleats. Now and
  then two of them square up, rear and butt heads (**BONK!**), and one leaps
  across the tops of the stats cards and takes a bite out of one. The bite
  grows back. The next-scan bar is grass being eaten, a goat munches beside
  the search box as you type, and one peeks over a stats card when you point
  at it. A new device arrives as a kid goat pronking with its name, a finished
  scan gets a bleat, an offline device's row gets headbutted, watched devices
  wear a goat bell that rings while they're up, and when everything's online
  the herd celebrates. On the Map, cables are rope and a goat stands on the
  router, king of the hill.
  The goats can baaah out loud, too. The 🔊 button that appears beside the
  theme button in this theme turns their bleats on. They're made in the
  browser, with no audio files, and they're off until you click it. The choice
  is remembered in your browser. Idle bleats are kept to one every eight seconds
  or so, while new devices, scans, headbutts and the celebration always get
  theirs.
- **Thunderstorm**: rain falls behind the page, in gusts that swing its angle,
  with clouds drifting along the top and splashes along the bottom. Every 20 to
  60 seconds a forked bolt of lightning cracks across the sky, which brightens
  softly, and the header shakes with the thunder a moment later. It never
  flashes the whole screen. The storm follows your network: each device that
  goes offline makes it heavier and the lightning more frequent, and its row
  flickers like a power cut. As they come back, the storm eases. A new device
  brings a bolt of lightning. On the Map, sparks run
  along your cables and offline devices drip.
  It can be heard, too. The 🔊 button that appears beside the theme button in
  this theme turns on rain, which gets heavier with the storm, and thunder
  after each strike: a sharp crack and a long rolling rumble when it's close,
  just a low far-off roll when it isn't. Like the goats, it's made in the
  browser with no audio files, off until you click it, and remembered in your
  browser. It goes quiet in a background tab.
- **Goat Night**: the Goat theme after dark. The same herd and the same
  jokes, on dark panels under a moonlit sky, with stars, a barn with a lit
  window and fireflies over the meadow. With sound on, crickets chirp between
  the bleats.
- **Night Street**: a city block after dark. A skyline stands behind the
  page, and its lit windows go up and down with how much of your network is
  up (the odd one flickers with a TV). Street lamps pool light on the pavement:
  one always buzzes, and moths circle a couple of them. Cars go by along the
  road at the bottom. A new device arrives by taxi with its name on the roof
  sign, a finished scan brightens every lamp and the Map's wiring, and a device
  going offline puts a lamp out for a moment.
- **Constellation**: the night sky, with your network written in it. Every
  device is a star with its own place in the sky, and the stars are joined by
  faint constellation lines. Unknown devices shine red and offline ones dim.
  The moon's phase is how much of your network is up: full when everything is.
  Behind them are the Milky Way, nebulae, a slowly turning spiral galaxy and a
  ringed planet. Stars twinkle, a satellite blinks across now and then, and
  shooting stars streak by. A finished scan brings a meteor shower. A new device
  arrives as a bright shooting star and becomes a star of its own, with its
  name beside it for a moment. A device going offline collapses in a flash. On
  the Map, star dust twinkles along your cables.
- **Claw Machine**: the prize cabinet at the arcade. Marquee bulbs chase
  under the header, the counts glow in red LED windows like the credit
  display, the next-scan bar is a candy-striped timer, and the search box is
  the coin slot. Behind the glass there's a heap of plush prizes (bears,
  bunnies, ducks, cats, frogs, stars and capsules), a prize chute and neon
  tubes up the sides. The cabinet's own claw plays by itself every so often:
  it rides the rail, drops into the heap, grabs, and usually lets the prize
  slip on the way to the chute. When it does win, the chute flashes. The 🕹
  button in the header sends a claw down onto one of your devices' rows. It
  comes back up with the device's name on a tag and either carries it off to
  **WINNER!** or drops it with a **SO CLOSE!**. A new device is always a win:
  **NEW PRIZE!** A finished scan puts a coin in, races the marquee and sets
  the cabinet playing. A device going offline gets a **TRY AGAIN** stamp and
  drops its plush, and one coming back gets a **BONUS!** Watched devices
  carry a little bear that bobs while they run, and unknown devices are
  mystery capsules, in the list and on the Map, where bulbs chase along the
  cables. With sound on (the 🔊 button, off until you click it) you get the
  coin, the gantry motor, the clunk of the claw, a jingle for a win and a
  sad trombone for a miss.
- **Hotdog Stand**: a tribute to Windows 3.1's loudest colour scheme. It's
  mustard yellow with ketchup-red title bars, in the bold system font, with a
  striped awning under the header. The next-scan bar is a sausage sliding into
  its bun, and steam rises off the stats. Now and then a squeeze of ketchup and
  mustard zigzags across the page, and a hot dog cart rolls along the bottom. A
  new device calls "Order up!", a finished scan rings "Ding!", and a device that
  goes offline gets its row stamped **86'd** (diner slang for "we're out"). On
  the Map, online devices get a squiggle of mustard, unknown ones a 🌭, and the
  router a paper hat. With sound on (the 🔊 button, off until you click it) the
  ding is a real desk bell, the cart rings a bicycle bell, the ketchup squirts,
  a new device gets a little jingle, and one that goes offline gets the chord
  every Windows 3.1 owner remembers.
- **Steampunk**: walnut and leather, brass and copper. The name sits on a
  riveted brass nameplate, the panels have rivets in their corners, and the
  counts glow like nixie tubes. A copper steam pipe runs under the header, and
  the next-scan bar is a glass pressure tube filling with amber. Brass gears
  turn slowly behind the page, and now and then an airship drifts past. When a
  scan finishes, the gears lurch forward and the valve lets off steam. A new
  device arrives by telegraph ticker tape, and a device that goes offline
  gets a hiss of steam on its row. On the Map, cables become copper pipes,
  running devices get a little turning cog, and the router wears a top hat.
  With sound on (the 🔊 button, off until you click it) the valve hisses when
  it lets off steam, the gears clank when a scan lands, a new device gets a
  steam whistle, a device that goes offline a dull clunk, and one that comes
  back a small bell. More brass fittings:
  - a **pressure gauge** in the header shows how much of your network is up,
    against the most seen online this session, and falls into the red and
    trembles if a lot drops off;
  - a **brass clock** keeps time on the Last scan card and chimes when a scan
    lands;
  - watched devices carry a **wind-up key** that turns while they run, and
    winds down when they go offline;
  - **Identify** feeds a punch card while it works, and a **port scan** spins a
    little orrery;
  - pointing at an address or MAC puts a **brass lens** over it;
  - the search box is a **telescope eyepiece**, and rows glint as you type;
  - **oil lamps** flicker softly in the corners;
  - the **Map** unrolls like drafting paper when you open it, and brass
    capsules shoot along its pipes with a thunk of air.

None of it runs while the tab is in the background. With reduced motion switched
on in your system settings, it all holds still: Matrix shows a still wall of
glyphs instead of rain.

### Holiday Spirit

Switch on **Holiday Spirit** under **Settings → Behaviour** (or set
`HolidaySpirit` to `true` in `appsettings.json`) and every dashboard dresses up
for the holidays:

- **Halloween theme** from **October 1st** to **31st**, back to its own theme on
  November 1st;
- **Christmas theme** from **December 1st** to **25th**, back on December 26th.

It goes by each browser's date and changes over by itself, even on a dashboard
left open. Your own theme isn't touched: it comes back when the season ends.
Pick another theme from the menu during a season and that browser keeps it
until the next season. Switching Holiday Spirit off puts every dashboard back
on its own theme.

### Night mode

Switch on **Night mode** under **Settings → Behaviour** and every dashboard
wears a night theme between two clock times, by each screen's own clock, then
goes back to its own theme in the morning. The defaults are 9 pm to 6 am and
**Night Street**; pick any hours and any theme, including a drop-in. Night
Street, Goat Night and Constellation were made for it, and Dark is the quiet
choice for a screen in a bedroom.

It works like Holiday Spirit: your own theme isn't touched and comes back at
dawn, and an open dashboard changes over on its own, checked once a minute.
Pick another theme from the menu during the night and that browser keeps it
until the next night; pick the night theme again and Night mode takes over.
During a Holiday Spirit season the holiday theme wins.

### Drop-in themes

A theme can also be a folder of its own, added without rebuilding BAMF. Put it
in the `themes` folder of the install (`C:\BAMF\themes\<name>\` on Windows,
`/opt/bamf/themes/<name>/` on Linux) and reload the dashboard. It appears in the
theme menu under **Installed**. Delete the folder and it's gone. Updates leave
`themes` alone.

```
themes/
  my-theme/          the folder name is the theme's id: a-z, 0-9 and -, up to 40
    theme.json       {"name": "My Theme", "swatch": ["#101820", "#f2aa4c", "#ffffff"]}
    theme.css        optional: its colours and styles
    theme.js         optional: its animations and reactions
```

**theme.css** sets the same colour variables the built-in themes use, scoped to
the theme's id:

```css
[data-theme="my-theme"] {
  --bg:#101820; --panel:#18222e; --panel-2:#1f2b3a; --line:#2c3a4d;
  --text:#e8eef5; --text-dim:#8a9bb0; --led-on:#4cd98a; --led-warn:#f2aa4c;
  --led-off:#3a4758; --danger:#ff6b6b; --focus:#f2aa4c; --radius:8px;
}
```

**theme.js** registers the theme and gets a small context to work with.
Everything it starts through the context stops when someone picks another
theme:

```js
BAMF.registerTheme("my-theme", ctx => {
  // ctx.root: a layer over the page that never takes a click
  // ctx.background(el): put an element behind the page
  // ctx.later(fn, ms), ctx.every(fn, ms), ctx.onStop(fn)
  // ctx.calm: true when reduced motion is on, so hold still
  // ctx.switched: true if the user just switched to this theme
  // ctx.hosts(), ctx.view(), ctx.headerBottom(), ctx.wentOffline()
  // helpers: ctx.svg(tag, attrs), ctx.rnd(a, b), ctx.pick(list), ctx.esc(text), ctx.nameOrIp(host)
  ctx.on("newDevice", host => { /* a new device appeared */ });
  ctx.on("scanDone", () => { /* a scan finished */ });
  ctx.on("rendered", () => { /* the dashboard redrew */ });
  ctx.on("netChange", (offIds, backIds) => { /* devices went offline or came back */ });
  ctx.on("decorateNode", (g, node) => { /* add SVG to a Map node as it's drawn */ });
});
```

A theme can't take a built-in theme's name. BAMF serves only those three files,
and only from folders with a valid name. A theme's script runs in the
dashboard, so adding one takes the same access to the server as editing
`appsettings.json`: it can't be done from the browser. The folder can be moved
with `Bamf:ThemesPath`.

## Compact rows

**Settings → Display → Compact rows** halves the height of each device row so
a busy network fits on one screen. Nothing is hidden; the secondary lines just
get tighter. Like the theme, it's kept in the browser rather than on the
server, so each device you open BAMF on can choose for itself.

## A network this machine isn't on

BAMF discovers devices through the ARP table, which only ever holds entries
for networks this machine has an interface on. A configured network it has
no address on is **skipped**: the log says so once, the network's tab carries
a **no interface** tag with no countdown, the Settings row says why, and the
header countdown ignores it. Nothing is probed there. Either remove it from
`Bamf:Subnets` or give the machine a NIC on that network; the moment one
appears, scanning starts on the next pass.

## Next scan, per network

The header's **next scan** countdown is the scanner's own schedule, not an
estimate. With several networks on different intervals it counts down to
whichever is due soonest and names it; pick a network tab and it counts down
to that one instead. Hover it for every network's figure. Each network tab
shows its own `~40s`, and the Settings table reads `every 90s · next in 62s`
per network, or `paused`. While a sweep is running it reads **scanning now**.
`GET /api/hosts` exposes the same as `subnetNextDue`, ISO-8601 UTC per
network.

## Linking to a tab

Each dashboard tab has its own address: `/#settings`, `/#activity`,
`/#home`, `/#forgotten`. The Devices view is the bare URL. Bookmark or pin
one and it opens straight to that tab; Back and Forward move between tabs
you've visited. Handy for a phone home-screen shortcut that goes straight to
**Who's home**, or a pinned Settings page.

## Wall display

**Tools → Wall display**, or open `/wall` directly: a status board for a TV
on the wall, a spare tablet on a shelf, or a monitor in the rack. Big clock,
how many devices are online out of how many, how many of those are unknown,
one square per device (green online, amber online and unknown, dark offline),
the last six comings and goings, and when the last scan ran. No controls, no
menus: a tap or a key press goes to the dashboard.

- `/wall?net=192.168.1.0/24` shows one network.
- It's black behind everything, so an OLED shows nothing where nothing is,
  and the board drifts a few pixels every minute so an older screen doesn't
  burn the numbers in. With reduced motion on it holds still.
- It refreshes every 15 seconds and says so if it loses BAMF. The pointer
  hides when it hasn't moved.
- Same password as the dashboard, if one is set. Kiosk browsers on a Pi or a
  Fire tablet work; so does "Add to home screen" on a phone.

## Desktop shortcut (Windows)

Run `windows\Install-DesktopIcon.bat` once. It puts a **BAMF** shortcut on your
Desktop with the burst icon; double-clicking it opens the dashboard, silently
starting the service first if it isn't running (no admin prompt when the
service is already up). The launcher and icon are copied to `C:\BAMF`, so
the shortcut survives updates.

## What an update preserves

Both updaters are built so an update never costs you anything. Your settings
live in three places, and it helps to know which is which:

| What | Where it lives | On update |
|---|---|---|
| Subnets, password, webhook URL, scan interval, ping tuning | `appsettings.json` | Copied aside and restored. The version's fresh defaults are written next to it as `appsettings.new.json` so you can merge in any new options. |
| Custom names, notes, watch stars, ignored/known flags, all online-offline and address history, **and everything saved from the Settings tab** | `bamf.db` | Never touched, and snapshotted to `backups/` first (last 30 kept). |
| Theme choice | your browser's localStorage | Not on the server at all, so nothing can disturb it. |

The second row is the one people don't expect: settings changed in the
dashboard are stored in the database, not the config file, and the database
value **overrides** `appsettings.json`. So if a setting seems to ignore your
config after an update, that's why - change it in the Settings tab, or use
**Reset to file defaults** there to hand control back to the file.

### Rolling back

The snapshots in `backups/` are ordinary SQLite files - restoring one is a copy:

```powershell
# Windows
Stop-Service BAMF
Copy-Item C:\BAMF\backups\bamf-20260808-231500.db C:\BAMF\bamf.db -Force
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

The `windows` folder contains `Update-BAMF.bat` + `update.ps1`. One-time setup:
copy both files somewhere permanent (the Desktop is fine). The updater builds
from the zip via a temp folder, so the only folder BAMF keeps on disk is
`C:\BAMF` - app, config, database, and backups all live there.

**Installs from before 1.2.2 lived in `C:\BAMFApp`, and the updater moves them
to `C:\BAMF` automatically**, once. It moves the whole folder, so your config,
database and backups come along untouched, then repoints the Windows service
and retargets the desktop shortcut. If a leftover `C:\BAMF` source folder from
an old version is in the way it's renamed to `C:\BAMF.old-<timestamp>` rather
than deleted; if `C:\BAMF` already holds a working install, the migration is
skipped and your existing folder is left alone.

From then on, updating is:

1. Download the new `BAMF.zip` (to Downloads).
2. Double-click `Update-BAMF.bat`. It elevates, stops the service, swaps the
   source, rebuilds, restores your `appsettings.json`, and restarts the service.
3. Ctrl+F5 the dashboard.

**Which source it builds:** an explicit `-ZipPath` is used as given. Otherwise
every candidate is ranked by the version it declares and the **highest wins**:
the source tree the script is sitting in (if it is one) and each `BAMF*.zip`
in Downloads. Versions are read from the `BAMF.csproj` inside each, so a
misleading filename can't win; a version in the filename is the fallback, and
a source too old to declare one ranks last. Ties go to the tree, which needs
no extracting. With more than one candidate it lists them and marks its choice:

```
==> Found 3 sources; choosing the highest version
     -> BAMF-1.19.0.zip                         version 1.19.0
        source tree C:\Users\you\Downloads\BAMF-1.17.0\BAMF   version 1.17.0
        BAMF-old.zip                            version unknown
```

Earlier versions of the script let the tree win outright whenever it sat
inside one. That quietly rebuilt an old extracted tree every time, reported
success, and ignored the newer package sitting next to it. Ranking by version
closes that.

**It never goes backwards by accident.** Rebuilding the version already
installed is allowed (that's how a config-only change is picked up), but if
the best source it can find is *older* than what's running it stops before
touching anything and says where to put the newer zip. To install an older
version on purpose, pass `-AllowDowngrade`. The script prints the version it's
about to install and the one it replaces, e.g.
`Installing version 1.19.0 (replacing 1.17.0)`.

The updater **follows the installed service**: it reads the service's binary
path and updates that folder, rather than assuming `C:\BAMF`. Older installs
often live elsewhere, and building into the default while the service still
pointed at the old folder used to look like a successful update that changed
nothing. It also verifies the build produced an exe and a `wwwroot`, prints the
version it installed and the path it's running from, and warns loudly if the
service still points somewhere else.

Your database and dashboard settings are never touched, and every update
snapshots `bamf.db` into `C:\BAMF\backups` first (last 30 kept). If an update ships new
config options, the fresh defaults are saved as `appsettings.new.json` next to
your kept config so you can merge anything interesting.

## Install as a Windows Service

Most people should just double-click `windows\Install-BAMF.bat`, which does all
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

## Docker

An image is published to GHCR with every release, for amd64 and arm64 (so a
Raspberry Pi works). It runs on the **host's network**: ARP only sees the
network the process is actually on, and a bridged container is on Docker's
network, not yours.

```bash
docker run -d --name bamf --restart unless-stopped \
  --network host --cap-add NET_RAW --cap-add NET_ADMIN \
  -v bamf-data:/data \
  -e Bamf__Subnets__0=192.168.1.0/24 \
  ghcr.io/rhc52980/bamf:latest
```

Then open http://localhost:8840. Or with Compose: copy `docker-compose.yml`
from the repo, edit the subnet, and `docker compose up -d`.

- **Configuration** — every key in `appsettings.json` can be an environment
  variable, with `__` in place of the colon: `Bamf__ScanIntervalSeconds=30`,
  `Bamf__Password=secret`, `Bamf__WebhookUrl=https://…`, a second network as
  `Bamf__Subnets__1=…`. Or mount your own file over `/app/appsettings.json`.
  Whatever you change in the Settings view is saved in the database, so it
  survives a new image.
- **Data** — the database lives in `/data` (the `bamf-data` volume above).
  Keep that volume across updates: `docker pull` the new tag, remove the old
  container, run the same command again.
- **Capabilities** — `NET_RAW` and `NET_ADMIN` are what the active ARP scan and
  the traffic monitor need; without them BAMF still runs on ping sweeps.
- **Themes** — drop-in themes go in `/app/themes` (mount a folder there).
- **Tags** — `ghcr.io/rhc52980/bamf:latest` follows the newest release;
  `:1.39.0` pins a version. There's a `Dockerfile` in the repo if you'd rather
  build it yourself: `docker build -t bamf .` from the repo root.
- **Health** — the container checks `/api/hosts` every minute, so
  `docker ps` shows it healthy or not.

## Using the API

Plain HTTP on the same port as the dashboard - no SDK, no tokens, no
negotiation. Everything below works from `curl`, a browser, a script, or
anything that speaks HTTP.

**Base URL**: `http://<server>:8840`

**Auth**: none unless you set `Bamf:Password`. If you have, every route needs
HTTP Basic auth with *any* username and that password:

```bash
curl -u x:yourpassword http://192.168.1.10:8840/api/hosts
```

**Reading.** `GET /api/hosts` is the full JSON picture - devices plus scan
metadata (`version`, `buildDate`, `subnets`, `lastScan`, per-network scan
modes). Each device carries an `id`, which is what the per-host routes take:

```bash
curl http://192.168.1.10:8840/api/hosts
curl http://192.168.1.10:8840/api/hosts.txt      # same devices, plain text
curl http://192.168.1.10:8840/api/events         # recent online/offline events
```

**Changing things.** POST with a JSON body:

```bash
# name a device (empty string clears it back to the DNS name)
curl -X POST http://192.168.1.10:8840/api/hosts/16/name \
  -H "Content-Type: application/json" -d '{"name":"Kevin PC"}'

# approve it, watch it for downtime, hide it
curl -X POST http://192.168.1.10:8840/api/hosts/16/known   -H "Content-Type: application/json" -d '{"known":true}'
curl -X POST http://192.168.1.10:8840/api/hosts/16/watch   -H "Content-Type: application/json" -d '{"watched":true}'
curl -X POST http://192.168.1.10:8840/api/hosts/16/ignore  -H "Content-Type: application/json" -d '{"ignored":true}'

# no body needed
curl -X POST http://192.168.1.10:8840/api/hosts/16/wake
curl -X POST http://192.168.1.10:8840/api/hosts/16/identify
```

**Scanning** is `GET` - it returns results rather than changing state:

```bash
curl "http://192.168.1.10:8840/api/hosts/16/portscan?ports=22,80,443"
curl "http://192.168.1.10:8840/api/portscan/ip?ip=192.168.2.50"
curl "http://192.168.1.10:8840/api/portscan/pattern?ip=*.245"
curl "http://192.168.1.10:8840/api/portscan?subnet=192.168.2.0/24"
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
| GET | `/api/hosts` | All hosts + scan metadata (includes the running `version`). Each host has `addresses`: every address it answers on, main first, each with `subnet`, `firstSeen`, `lastSeen`, `current` and `linkUrl` |
| GET | `/api/hosts.txt` | The same devices as a plain-text fixed-width table — no JSON, no markup. For `curl`, a terminal, or pointing a read-only agent at |
| POST | `/api/hosts/{id}/identify` | On-demand device fingerprint: one ICMP echo for the TTL plus a short fingerprint-port probe. Returns the guess, TTL, and open ports, and saves the guess |
| POST | `/api/hosts/{id}/known` | Body `{"known": true}` — approve/unapprove a host |
| POST | `/api/webhook/test` | Send a test notification to `Bamf:WebhookUrl` (the dashboard's Test webhook button). Returns `{ok:true}` or `{ok:false,error:"…"}` |
| POST | `/api/settings/active-arp` | Body `{"enabled": true}` — toggle active ARP scanning at runtime |
| POST | `/api/settings/auto-ignore-random` | Body `{"enabled": true}` — toggle auto-ignoring of randomized MACs at runtime |
| POST | `/api/settings/webhook` | Body `{"url": "https://..."}` — save the notification webhook (empty string clears it). Returns a masked form; the full URL is never read back |
| POST | `/api/settings/update-check` | Body `{"enabled": true}` — toggle the daily GitHub update check. Turning it on checks immediately and returns the result |
| POST | `/api/hosts/{id}/watch` | Body `{"watched": true}` — watch a host for downtime (star toggle in the UI) |
| POST | `/api/hosts/{id}/ignore` | Body `{"ignored": true}` — hide a host from main views and suppress its alerts/history |
| POST | `/api/hosts/{id}/note` | Body `{"note": "..."}` — save a free-text note (max 500 chars) |
| POST | `/api/hosts/{id}/link` | Body `{"link": "8006"}` — per-host link override: bare port, `:port/path`, or a full URL with an optional `{ip}`. Empty clears it. Returns the resolved `linkUrl` |
| POST | `/api/hosts/{id}/name` | Body `{"name": "Kevin's PC"}` — set a friendly name (empty string clears it). In the UI, click a host's name to edit it. |
| POST | `/api/hosts/{id}/wake` | Send a Wake-on-LAN magic packet to the host (button appears on offline hosts) |
| GET | `/api/hosts/{id}/portscan` | On-demand port check for one host. Optional `?ports=22,80,8000-8100` for a custom set (default: ~18 common ports) |
| GET | `/api/portscan` | On-demand port scan across online hosts. Optional `?ports=...` and `?subnet=...` |
| GET | `/api/portscan/ip` | On-demand port check for any IP: `?ip=192.168.1.50`, optional `?ports=...`. The address need not be a known host; private ranges only (400 otherwise) |
| GET | `/api/portscan/pattern` | Wildcard scan: `?ip=*.245`, optional `?ports=...`. Expands only across configured subnets, capped at 256 addresses |
| GET | `/api/events` | Network-wide activity feed (recent online/offline events, all hosts) |
| GET | `/api/hosts/{id}/events` | One host's online/offline event history |
| GET | `/api/layout` | The recorded layout as one JSON file: switches and their kinds, uplinks and port labels; each device's placement, name, note, link, flags, type, tags and combined cards; declared gateways; type icons; Map positions. Devices are keyed by MAC and switches by their place in the file |
| POST | `/api/layout` | Body: a file from `GET /api/layout`. Replaces the recorded layout. Returns `{"devices", "switches", "skipped"}`, `skipped` being the MACs this server hasn't seen, whose settings wait for a later import |
| GET | `/api/hosts/{id}/timeline` | One device's story, newest first: `[{"at", "kind", "text"}]`, kinds `first`, `online`, `offline`, `address`, `port`, `portclosed` and `alert:<kind>` |
| GET | `/metrics` | Prometheus text exposition: see [Prometheus metrics](#prometheus-metrics). `/api/prometheus` redirects here |
| POST | `/api/hooks/scan` | Ask for a scan now, of every network or `?subnet=192.168.1.0/24`. See [Inbound webhooks](#inbound-webhooks) |
| POST | `/api/hooks/wake/{mac}` | Send a Wake-on-LAN packet to a MAC (`AA:BB:…`, `AA-BB-…` or `AABB…`), known to BAMF or not |
| GET | `/api/remotes` | The other BAMF servers being watched, their status, and their devices |
| GET | `/api/alerts` | Alerts BAMF raised, newest first: rules, ports, DHCP and DNS, each `{"at", "kind", "title", "detail"}` |
| GET | `/api/settings/rules` | The alert rules, quiet hours and port watch: `{"rules": [...], "quiet": {"from", "to", "digest", "now", "held"}, "portWatch"}` |
| POST | `/api/settings/rules` | Body: the whole rule list, each `{"id", "name", "kind": "offline"\|"online"\|"hours", "target": "any"\|"watched"\|"tag:kids"\|"host:12", "minutes", "from", "to", "enabled"}`. `id` empty for a new rule |
| POST | `/api/settings/quiet` | Body `{"from": "23:00", "to": "07:00", "digest": true}` — quiet hours in the server's local time; empty times clear them |
| POST | `/api/settings/port-watch` | Body `{"enabled": true}` — scan every online known device's common ports daily at 4 am |
| POST | `/api/settings/night` | Body `{"enabled": true, "from": "21:00", "to": "06:00", "theme": "nightstreet"}` — Night mode: every dashboard wears that theme between those clock times. `GET /api/settings` returns it as `editable.night`; `GET /api/hosts` as `night` |
| GET | `/wall` | The wall display page. `?net=<cidr>` shows one network. See [Wall display](#wall-display) |
| POST | `/api/ports/watch` | Run that scan now; returns how many newly open ports it found |
| GET | `/api/hosts/{id}/ports` | Every port found open on a device, open now or once: `{"port", "service", "firstSeen", "lastSeen", "open"}`. A port scan (`/api/hosts/{id}/portscan`, which now returns `{"ports", "newlyOpen"}`) records here |
| GET | `/api/hosts/{id}/traffic` | Bytes per hour for a device over the last 7 days (`?days=` for more): `[{"hour", "rx", "tx"}]` |
| POST | `/api/settings/report` | Body `{"schedule": "daily", "hour": 8, "day": 1}` — the scheduled report: `off`, `daily` or `weekly`, the hour (0–23, the server's local time) and, for weekly, the day (0 Sunday to 6 Saturday) |
| POST | `/api/reports/send` | Send the report now, whatever the schedule; returns what was sent |
| GET | `/api/reports/preview` | The report as it would be sent |
| GET | `/api/traffic` | The traffic monitor's status, the top talkers with their five-minute strips, the DHCP and DNS servers seen, and the watch alerts. `GET /api/hosts` carries each host's `traffic` (`rx`, `tx` bytes per second; `rxTotal`, `txTotal`) and `dns` (the servers it asks) |
| POST | `/api/traffic/trust` | Body `{"kind": "dhcp", "ip": "192.168.1.1", "trusted": true}` — trust a DHCP or DNS server so it never alerts, or forget it so it counts as new again |
| GET | `/api/hosts/{id}/latency` | One host's latency samples over the last 24 hours (`?hours=` for more): `[{"at", "ms"}]`, `ms` null where the echo went unanswered. `GET /api/hosts` carries each host's latest as `latencyMs`, and its uptime over 7 and 30 days as `uptime7` and `uptime30` (percent) |
| POST | `/api/hosts/{id}/tags` | Body `{"tags": ["kids", "IoT"]}` — replace a device's tags (up to 20, each up to 24 characters, no commas). `GET /api/hosts` lists each host's `tags` |
| GET | `/api/hosts/{id}/ips` | One host's address history: each main address it has had, with when it began and ended |
| POST | `/api/hosts/{id}/forget` | Body `{"forgotten": true}` — soft-delete to the Forgotten tab (reversible) |
| DELETE | `/api/hosts/{id}` | Permanently delete a host and its history (from the Forgotten tab) |
| POST | `/api/switches` | Body `{"kind": "switch", "name": "Office SG108E", "ports": 8, "subnet": "192.168.1.0/24", "hostId": 0, "uplink": "switch", "uplinkSwitch": 1, "uplinkPort": 16}` — add a switch, router or access point to the recorded layout. `kind` is `switch` (the default), `router`, `ap`, `virtual`, `ssid` or `vpn`; a virtual switch takes `runsOn` (the id of the machine it runs on) instead of a device, uplink or port count, and a wireless SSID takes `runsOn` as the id of its access point (a switch record of kind `ap`), with an optional `subnet` for its own network. Devices on a virtual switch, SSID or VPN are recorded with port 0. `uplink` is `""` (not recorded), `"router"` or `"switch"`. With a `hostId`, the network comes from that device. Returns the switch, or 400 with `{"error": "…"}` |
| POST | `/api/switches/{id}` | Same body — update a switch. Refuses loops and ports that would strand recorded devices |
| DELETE | `/api/switches/{id}` | Delete a switch. Devices recorded on it go back to unrecorded; switches plugged into it lose that uplink |
| POST | `/api/switches/{id}/ports` | Body `{"ports": [{"hostId": 3, "port": 1}, {"hostId": 4, "port": 2}], "labels": [{"port": 1, "label": "Living Room"}]}` — set everything on one switch at once. Hosts listed are placed on it (moving off any other switch; `port` 0 = not recorded), and hosts on it that aren't listed come off it. `labels`, when given, replaces the ports' locations (up to 40 characters; blank clears one); leave it out to keep them. Each switch in `GET /api/hosts` carries them as `portLabels` |
| POST | `/api/hosts/{id}/blink` | Body `{"seconds": 30}` (optional, 5–60) — "Find port": send the device bursts of UDP traffic, one second on and one second off, so its switch-port light pulses. Private addresses only; replaces any blink already running. Returns `until` |
| POST | `/api/map/positions` | Body `{"subnet": "192.168.1.0/24", "positions": {"s:1": [120, 140], "h:7": null}}` — save where nodes sit on the topology Map for one network. Keys are `h:<host id>`, `s:<switch id>`, `gw`, `self`, `net` and `box`. A null position forgets that node, so it goes back to the automatic layout. `GET /api/hosts` returns them all as `mapPositions` |
| DELETE | `/api/map/positions?subnet=…` | "Auto-arrange": forget every saved position on one network |
| DELETE | `/api/blink` | Stop a running Find port blink. While one runs, `GET /api/hosts` reports it as `blink` (`hostId`, `started`, `until`) with the server's `serverTime`; bursts are on for [2k, 2k+1) seconds after `started` |
| POST | `/api/hosts/{id}/type` | Body `{"type": "nas"}` — set a device's type, overriding the guess for its icon and type chip. Types: `router`, `switch`, `ap`, `camera`, `printer`, `tv`, `speaker`, `phone`, `tablet`, `laptop`, `desktop`, `server`, `nas`, `vm`, `game`, `iot`, `light`, `plug`, `device`. Empty goes back to the guess. `GET /api/hosts` returns it as `deviceType` |
| POST | `/api/settings/type-icons` | Body `{"icons": {"Linux": "server"}}` — the icon for every device of a guessed type; an empty icon clears it. Returned in `GET /api/hosts` as `typeIcons` |
| POST | `/api/hosts/{id}/combine` | Body `{"parentId": 12}` — combine the device into another as one of its network cards; `parentId` 0 separates it again. `GET /api/hosts` gives each host's `interfaceOf` (0 for a device of its own). Returns 400 with `{"error": "…"}` for a device combined with itself, or one that's a switch's own device |
| POST | `/api/hosts/{id}/gateway` | Body `{"ip": "192.168.1.1", "enabled": true}` — declare the device the gateway of the network that address is on (one of its own addresses), or stop declaring it. One gateway per network. `GET /api/hosts` lists them as `gateways`: `[{"subnet", "hostId", "ip"}]`. Returns 400 with `{"error": "…"}` for an address the device doesn't have, or a VPN's device |
| POST | `/api/hosts/{id}/plug` | Body `{"switchId": 1, "port": 3}` — record which switch port a device is plugged into. `switchId` 0 clears it; `port` 0 means "port not recorded". `GET /api/hosts` returns each host's `switchId` and `switchPort`, and the layout as `switches` |

## Device links and port check

Each host's IP is a link to that device's web UI - handy for routers, NAS
boxes, printers, and cameras.

**Where it points is configurable**, because a real network rarely runs
everything on port 80:

- **Per device** - **Set link…** in a host's ⋯ menu. Accepts a bare port
  (`8006` → `http://<ip>:8006`), a port with a path (`:8006/admin`), or a full
  URL with an optional `{ip}` placeholder (`https://{ip}:8443`). A small ⇗
  marks devices with their own link. Like names and notes, it's bound to the
  MAC, so it survives the device changing IP.
- **For everything else** - `Bamf:DeviceLinkTemplate` in `appsettings.json`,
  default `http://{ip}`. Set it to `https://{ip}` or `http://{ip}:8080` and
  every device without its own link follows.

Only `http` and `https` links are accepted; anything else falls back to
`http://<ip>` rather than becoming a clickable link. The **Ports** button
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
using only data it already holds: the vendor (from the MAC's OUI), the
hostname (from reverse DNS or NetBIOS), and the services a device has announced
over mDNS. A device saying what it is outranks a guess from its vendor alone,
so an Apple-vendor device that announces `_airplay._tcp` and
`_companion-link._tcp` becomes "Apple TV / HomePod (mDNS)" rather than "Apple
device (vendor)".

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

Press `/` anywhere on the page to jump to the search box. `Esc` in the box
clears the filter; elsewhere it closes whatever is open — a dialog, a row
menu, the Tools menu, an expanded row.

## Network map

The **Map** tab draws each network as a diagram. Pick a network tab to see
just that one. It has its own address, `/#map`, like the other tabs. There are
two layouts, chosen above the map and remembered in your browser:

- **Topology** (the default): the router at the top and BAMF's own machine
  beside it. Your recorded switches sit below, chained as they're cabled, each
  with its devices listed underneath in port order and labelled with the port
  and its location (`6 · Living Room`). Everything not recorded on a switch is
  in an **On this network** box. Every device has an icon for its kind (router,
  switch, access point, camera, printer, TV, speaker, phone, tablet, laptop,
  desktop, server or NAS, smart-home gadget), taken from its device guess and
  its names.
- **Radial**: each network drawn around itself, devices round the edge grouped
  by type. This was the original map.

Hover anything for details. Click a device to record where it's plugged in,
or a switch to open its Ports dialog.

**The whole network at once.** With **All networks** picked, a second toggle
switches between **Per network** (a card each) and **Whole network**: one
diagram of your entire setup, drawn by the cabling you recorded rather than by
network. One switch often carries devices from several networks, and this is
where that shows.

- Each network has a colour, listed under the legend, and every device carries
  a dot in its network's colour (a device on several networks, one dot each).
- Nothing is drawn twice. BAMF, and a router with an address on every network,
  appear once.
- Each network's router or gateway is a top of its own, side by side. Networks
  that share a router hang from it together. A second router recorded on a
  switch's port is drawn there, with its network hanging from it.
- Devices not recorded on a switch go in a box per network, named after it.
- It has its own saved arrangement, so dragging things there doesn't move them
  on the per-network cards. Dropping a device on a switch or in a box works as
  it does anywhere else.

It's a topology drawing only: picking it switches Radial back to Topology.
Your choice is remembered in your browser.

**One router, several networks.** Declare a router the gateway of more than
one network (see [Gateways](#gateways)) and, with **All networks** and
**Per network** picked, those networks get one card between them instead of
one each: **Networks behind** the router, with every one of them hanging off it,
each in its own colour. It works like the whole-network view, but only for
that router's networks and the switches on them, and it keeps its own saved
arrangement. Networks with a gateway of their own keep their own cards.

**For the closet door.** **Print** on any topology map opens a clean white
page: the whole drawing fitted to one landscape sheet, black lines and text, a
title, the date and device count, and a legend. Nothing a theme adds comes
along. The browser's print dialog opens on it; save it as a PDF from there if
you'd rather. (If the page doesn't appear, the browser blocked the pop-up:
allow pop-ups for BAMF.)

**Room to see it all.** A topology map grows as tall as its drawing needs,
up to nearly the height of the window, so a big network isn't squeezed into a
strip. **⛶ Full screen** on any topology map fills the whole screen with it,
and Esc or **Exit full screen** puts it back.

**Arranging the topology.** Drag anything wherever you like. It's saved on the
server per network, so the layout is the same in every browser. Anything you
haven't moved by hand follows the node it hangs from, so dragging a switch
brings its devices along. Zoom with Ctrl + scroll, a pinch or the − / +
buttons; a plain scroll wheel keeps scrolling the page, so you can scroll past
one network's map to the next. Drag the background to pan,
**Fit** shows everything, and **Auto-arrange** forgets the positions you set on
that network.

**Wiring it up by dragging:**

- **Drop a device on a switch** to record it there. The Plugged into dialog
  opens with that switch picked, so you choose the port, or use Find port.
- **Drop a device in the On this network box** to unplug it.
- **Drop a switch on another switch, or on the router,** to set what it's
  plugged into.

Cancelling any of these leaves things as they were.

It is deliberately honest about what BAMF knows. ARP says which devices are
**present** on a network, not how they're cabled, so a thin line on the map
means "on this network" and nothing more. BAMF never works out on its own that
one device plugs into another. Two things are marked:

- **the gateway**: the one you declared, or else the one in this machine's own
  routing table (see [Gateways](#gateways)), and
- **this machine** (BAMF), from its own address on the network. It's drawn even
  when a ping sweep never sees it, which it usually doesn't. Record which switch
  it's plugged into and it's drawn there, with that switch's devices, labelled
  **BAMF**, instead of on its own beside the gateway.

### Gateways

BAMF on its own only knows one kind of gateway: the **default gateway in this
machine's routing table**. That's the router this machine sends its traffic
through, and only on the network it does that on. On any other network, BAMF
has no idea which device is the gateway. (A device whose guess says "router" or
"gateway" is only offered **Make this a router…** in its ⋯ menu; that's a hint,
not a gateway.)

So you can say. **Gateway…** in a device's ⋯ menu lists each of its addresses
with a checkbox: tick one to declare the device the gateway of that address's
network.

- A router with an address on each of your networks is the gateway of each:
  tick them all, and the Map draws those networks together, hanging off it.
- Each network has one gateway. Declaring another device there replaces the
  first. A declared gateway wins over the routing table.
- The dialog shows each network's gateway now, and where BAMF got it.
- Port scans go easy on a declared gateway, as they do on the routing table's.
- A VPN is never a gateway: a VPN's device can't be ticked, and a declared
  gateway can't be made a VPN.

A network with more than sixty devices switches to several rings and moves the
names into the tooltips, so the drawing stays readable. `GET /api/hosts` carries
the same facts as `networkPlaces`: per network, this machine's `selfIp` and
`selfMac` there, and the routing table's `gateway` on it. Declared gateways are
in `gateways`.

### Switches and cabling, as you record them

What BAMF can't discover, you can tell it. Add your switches, and your router
and access points, under **Settings → Switches and routers**, or with
**+ Switch / router** on a network's card on the map, which picks that network
for you. Each one has:

- a **type**: switch, router, access point, virtual switch, wireless SSID or
  VPN;
- a name, and how many ports it has (not asked for an access point, an SSID,
  a virtual switch or a VPN);
- what it's plugged into: the router, a port on another switch or router, or
  not recorded.

They all work the same way, with ports, locations, the Ports dialog and Find
port. The type sets the icon, and one thing more:

- **A router linked to the network's gateway becomes the top of the map,** with
  its own LAN ports. Devices and switches plugged straight into it get their
  port and location on the cable, like a switch's. Pick **Router** in the dialog
  and the gateway device BAMF sees is filled in for you.
- **An access point's devices with no port** are drawn as its Wi-Fi clients.

### Access points and wireless SSIDs

An access point isn't asked for a port count or its devices: devices reach it
over the air. Instead, give it a **wireless SSID** for each network it
broadcasts, like "Home" and "Guest". Use **Add a wireless SSID** in its dialog,
**Add SSID** beside it in Settings, **Add a wireless SSID…** in its device's ⋯
menu, or pick the **Wireless SSID** type and choose its access point.

- An SSID has its own network, since a guest SSID usually puts its devices on
  a different one from the main SSID. It defaults to the access point's.
- Its **Devices** dialog is a checklist of every device BAMF sees, from any
  network, like a virtual switch's VMs. **Plugged into…** on a device offers
  SSIDs too, with no port to pick.
- **On the Map** an SSID works like a virtual switch: it hangs off its access
  point on a dashed link, with its devices listed under it. Each device is
  connected by a **lightning bolt**, to show it's wireless.
- Deleting an access point deletes its SSIDs, and their devices go back to not
  recorded. An access point can't be changed to another type while it has SSIDs.

### VPNs

A VPN server is a device, like a router, and like a router it can answer on
several addresses at once: its LAN address and its tunnel address, say. Record
it with the **VPN** type. Pick it as its device and record what it's plugged
into, as for any switch. Then tick its **clients** in its **Clients** dialog:
devices on any network, each with however many addresses it has.

- **On the Map** its clients hang off it on dotted **tunnel** lines, not cables.
- On another network's card, a VPN with clients there stands on its own, marked
  with the network it's on, and nothing it's plugged into comes with it.
- **A VPN is never a network's gateway, and it doesn't join networks
  together.** It's never the top of a map, its device can't be declared a
  gateway, and nothing can be cabled into it.

### Virtual switches and VMs

A Proxmox or ESXi box, or a Hyper-V host, has a switch *inside* it: Proxmox's
`vmbr0`, ESXi's `vSwitch0`, a Hyper-V external switch. Its VMs plug into that,
not into a physical port. Record it as a **Virtual switch**, from
**Add a virtual switch on this…** in the host's ⋯ menu, or with that type in
the usual dialog. You pick the machine it **runs on**; there's no port count,
device or uplink to fill in, because its traffic leaves through that machine's
cable.

- **On the map** it hangs off its machine with a dashed link. Its VMs are listed
  underneath and labelled **VM**, so a Proxmox box plugged into your switch
  reads `switch → port 3 → proxmox → vmbr0 → VMs`.
- **Its VMs dialog** is a checklist, not a port list. It lists devices from
  every network, the machine's own first: a hypervisor often bridges VMs onto
  other networks or VLANs. A VM on another network is drawn on that network's
  Map under the virtual switch, labelled with the machine it runs on.
- **Find port** on a VM pulses the port of the machine it runs on, which is the
  physically true answer.

**Recognising VMs.** Hypervisors give virtual network cards well-known MAC
prefixes: Proxmox `BC:24:11`, QEMU/KVM `52:54:00`, VMware `00:50:56` and
`00:0C:29`, Hyper-V `00:15:5D`, VirtualBox `08:00:27`, Xen `00:16:3E`,
Parallels `00:1C:42`, and Docker `02:42`. A device with one of them gets a
device guess such as *Virtual machine (Proxmox MAC)* and a VM icon. The VMs
dialog marks those devices and lists them first, with **Tick suggested** to
take them all in one click. It's a suggestion; nothing is recorded until you
save.

QEMU/KVM and Docker use *locally administered* MACs, the same kind a phone
makes up when it randomises its address. So they're now named as virtual NICs
and never taken for randomising phones. Before 1.28, with **Ignore devices with
randomised MACs** on, such VMs were filed under Ignored. Any already there stay
until you unignore them.

**What BAMF can't see:** only *bridged* VMs, the ones with their own address on
your network, appear at all. VMs behind NAT, or on an internal-only switch,
never show up on the LAN, so no scanner can find them.

If one has an address BAMF sees, pick it as its device, and the map shows it
online or offline. A device that is itself one of these has
**Make this a switch or router…** in its ⋯ menu.

To say what's plugged in where, **click a switch on the map**, or use **Ports**
next to it in Settings. Its Ports dialog lists every port with a picker of your
devices, grouped by network, and saves the whole switch at once. A device
already on another switch is labelled with where it is, and moves when you
save. Picking one device on two ports is caught before anything is saved. A
new switch opens its Ports dialog as soon as you add it.

Each port also has a **Location**: where its cable goes, like "Living Room" or
"Play Room". It belongs to the port, not the device, so it stays put when you
move a device. It shows wherever a port does: `port 3 · Living Room` in the port
pickers, the map's tooltips and a switch's uplink, in brackets in
`/api/hosts.txt`, and as `portLocation` in the CSV export. If you lower a
switch's port count, the locations on the ports that go away are kept, and come
back if you raise it again.

For one device at a time, **click it on the map**, or use **Plugged into…** in
its ⋯ menu, to pick its switch and port. **Find in list** in that dialog jumps
to the device.

**Don't know which port it's on?** Use **Find port** in the Plugged into dialog,
**Find port…** in the device's ⋯ menu, or the picker at the top of a switch's
Ports dialog. For 30 seconds BAMF sends that device bursts of traffic, one
second on and one second off. A switch sends a device's traffic only out of
that device's own port, so its activity light pulses in a steady rhythm you
can spot against normal flicker. Pick that port and save.

Two things to know. The port BAMF's own machine is plugged into, and any cable
towards the router or another switch, pulse too, because the traffic passes
through them. And a Wi-Fi device pulses its access point's port. This works on
any switch with activity lights, unmanaged ones included, and needs no switch
password. It's small UDP packets to the device's discard port (9), a few hundred
a second, and only while a Find port is running. Only devices on a private
address can be blinked, one at a time.

**The dashboard pulses in step.** The bursts run on a fixed schedule from the
moment the blink starts, and BAMF tells every open dashboard when that was. So
while the switch light pulses, the device's dot on the Map gets a ring that
fills on each burst, its row in the list pulses the same way, and so does the
dot next to the countdown. Look from the switch to the screen, and if they
pulse together, you're looking at the right port. Closing the dialog leaves
the blink running so you can watch the Map. A small bar at the bottom of the
screen keeps the countdown and a **Stop** button, and Esc stops it too. A blink
started from another browser, like your phone, pulses here as well.

On the map, switches sit inside the ring, and every device you've recorded sits
in its switch's arc in port order, joined by a heavier **cable** line. Devices
you haven't recorded keep the thin "on this network" line to the middle, so the
map only shows cabling where you've said what it is. The port pickers show
what's already recorded on each port, so they double as a port list.

This is your record, not a measurement. Nothing here connects to a switch or
stores switch credentials, and BAMF can't tell when a cable moves. It works
with any switch, including unmanaged ones and budget "smart" switches such as
TP-Link's Easy Smart line, which can't report which device is on which port.
Only switches with SNMP or a visible MAC address table can, and BAMF doesn't
ask them.

## Setting a device's type and icon

BAMF's device type is a guess, and so is the icon the Map draws from it. To
overrule it, click the device's **icon** on the topology Map (clicking its name
still opens Plugged into; with the device focused, **T** does the same), or use
**Type / icon…** in its ⋯ menu. Pick from Router, Switch, Access point, Camera,
Printer, TV / media, Speaker, Phone, Tablet, Laptop, Desktop, Server, NAS,
Virtual machine, Game console, Smart home, Light, Smart plug or Other.
**Automatic** goes back to the guess.

A type you set:
- decides the device's icon on the Map;
- decides which **Device type** chip it's counted under;
- shows in the device list as "NAS · your type";
- is found by search;
- shows in `/api/hosts.txt` and the CSV export.

**One icon for a whole guessed type.** Tick "use this icon for every *Linux*
device" in the same picker, and every device BAMF guesses as Linux gets that
icon. **Settings → Device icons** lists these choices, with **Change** and
**Reset**. A type set on one device always wins over them.

## Filtering by device type

Under the status tabs, a **Device type** row lists the guesses actually
present in what you're looking at, each with a count: `Apple device 4`,
`Printer 2`, `no guess 26`. Click one to show only those, click it again (or
press `Esc`) to clear. The counts come from the network and status filters
already applied, so they don't collapse as you move between chips.

The chips are the guesses themselves with their evidence trimmed off -
`Apple device (vendor)` and `Apple device (mDNS)` are one chip - so there's no
separate category list to get out of step with what BAMF actually reports.
The row is hidden when everything in view shares one type. **no guess** is the
useful one: it's the list of devices worth pointing **Identify** at.

## Plain-text device list

`GET /api/hosts.txt` returns the whole device table as fixed-width plain text —
no JSON, no buttons, nothing to parse around:

```bash
curl http://<server>:8840/api/hosts.txt
```

Rows are sorted by network then numeric IP, so two fetches diff cleanly. Handy
in a terminal, and a tidy read-only way to hand an AI agent an accurate picture
of the network. It respects `Bamf:Password` like every other route.

## Notifications

**Settings tab → Notifications** (or **Tools ▾ → Notifications…**, which takes
you there) — paste a webhook URL, pick a format, hit **Save & test**. No config
edit, no service restart.

Four formats, chosen in the dialog (or with `Bamf:WebhookFormat`):

| Format | What BAMF sends | Your URL |
|---|---|---|
| **Auto** (default) | A rich Discord embed for a Discord URL; the generic JSON body for anything else | either |
| **ntfy** | Plain text with `Title`, `Priority` and `Tags` headers, the way ntfy expects | your topic, e.g. `https://ntfy.sh/bamf-alerts` — add `?auth=…` if the topic needs a token |
| **Gotify** | Gotify's `{title, message, priority}` JSON | your server's `/message?token=…` |
| **Generic JSON** | `{content, message, mac, ip, …}` — `content` and `message` both carry the text, so most simple endpoints show it | anything |

Priorities: a new device or an offline alert is high (ntfy 4, Gotify 8); a
recovery or a test is normal. ntfy alerts carry emoji tags so the notification
shows a 🔴 for offline and a 🟢 for recovered without any setup on your side.

### Alert rules and quiet hours

Watched devices alert the moment they drop or return. **Settings → Alert
rules and quiet hours** adds patience, groups and hours:

- **Offline for more than N minutes**, for any device, the watched ones, a
  tag, or one device. "The NAS offline for more than 15 minutes" alerts once
  per outage, not for a blip.
- **Back online**, for the same targets.
- **Online between** two times of day. "Devices tagged kids online between
  22:00 and 06:00" alerts once per device per day.

Each rule can be paused or deleted, and every alert it raises shows under
**Activity → Alerts** as well as going to the webhook.

**Quiet hours** hold every alert BAMF would send (new device, watched device,
rules, ports, DHCP and DNS) between two times of day, then deliver what was
held as one summary when the quiet ends: "While it was quiet: 3 alerts". Untick
the summary if you'd rather they were dropped. Scheduled reports keep their
own hour. Times are the server's local time.

### Port history and change alerts

Every port scan now leaves a record: each port found open on a device, when
it was first and last seen open, and whether it was open at the last scan.
The History panel lists them, with ports that have since closed struck
through. When a scan finds a port open that wasn't open the last time, that's
an **alert**: "New open port on camera: 23 (Telnet)", under Activity → Alerts
and to the webhook.

**Watch ports daily** (off by default, since a scan is active rather than
passive) scans every online known device's common ports at 4 am, so a port
that opens is noticed without anyone running a scan. **Scan now** runs the
same scan on demand.

### Prometheus metrics

`GET /metrics` serves the text format Prometheus scrapes, under the same
password as the dashboard if one is set:

- `bamf_devices_total` and `bamf_devices_online`, per network;
- per device (labels `mac`, `name`, `ip`, `network`): `bamf_device_online`,
  `bamf_device_latency_ms`, `bamf_device_uptime_7d_percent`, and
  `bamf_device_rx_bytes_total` / `bamf_device_tx_bytes_total` while the
  traffic monitor runs;
- `bamf_last_scan_timestamp_seconds`, `bamf_traffic_monitor_running`, and
  `bamf_info{version}`.

A scrape every 30 or 60 seconds is plenty; nothing here is computed on the
scrape beyond reading what the last scan left.

### Inbound webhooks

The way in, to go with MQTT going out:

- `POST /api/hooks/scan` makes every network due now, or one with
  `?subnet=192.168.1.0/24`. The scanner wakes straight away.
- `POST /api/hooks/wake/{mac}` sends a Wake-on-LAN magic packet to that MAC,
  directed at its network's broadcast address when BAMF knows the device.

Set `Bamf:HookToken` and both require it, as an `X-BAMF-Token` header or
`?token=`. With a `Password` set as well, the token stands in for the password
on these two endpoints, so a Home Assistant `rest_command` needs only the
token. Without a token they're as open as the rest of the dashboard.

```yaml
rest_command:
  wake_desktop:
    url: "http://bamf.local:8840/api/hooks/wake/AA:BB:CC:DD:EE:FF?token=…"
    method: post
```

### Other BAMF servers

A BAMF at another site can show in this dashboard, read-only. List it in
`appsettings.json`:

```json
"Remotes": [
  { "Name": "Cabin", "Url": "http://10.0.0.5:8840", "Password": "" }
]
```

BAMF fetches each remote's `/api/hosts` once a minute. Its devices appear
under network tabs named after it ("Cabin · 10.0.0.0/24"), with a **remote**
tag on the tab and a site chip in place of the ⋯ menu, since nothing can be
changed from here. **All networks** stays this server's own. A remote that
can't be reached keeps its last answer and its tab says **stale**; the
**Set in appsettings.json** card shows each remote's state. The Map draws
this server's networks only.

### Scheduled reports

Under **Settings → Notifications → Scheduled report**, pick **daily** or
**weekly**, a day for weekly, and an hour (the server's local time). At that
time BAMF sends a summary to the same webhook the alerts use:

- how many devices there are and how many are online;
- **new devices** first seen in the period (the last 24 hours, or the last 7
  days for a weekly report);
- devices that **went away**: went offline in the period and are still off;
- the **flakiest** device, the one that dropped most often;
- the **longest offline** known device, and for how long;
- the **least reliable**, by 7-day uptime;
- any DHCP or DNS **watch alerts**;
- the **top talkers**, when the traffic monitor is running.

On Discord it's an embed with a field per item; on ntfy, Gotify and generic
webhooks it's text. **Preview** shows what would go out, and **Send now** sends
it straight away. The API has `/api/settings/report`, `/api/reports/send` and
`/api/reports/preview`.

### Discord

In Discord: *Server Settings → Integrations → Webhooks → New Webhook*, pick a
channel, **Copy Webhook URL**, paste it in. BAMF saves it, immediately fires a
test message, and tells you whether the endpoint accepted it — so a typo shows
up right there instead of the first time something happens on your network.

You'll then get:

- an **amber** card when a new unknown device appears
- a **red** card when a ⭐ watched device goes offline
- a **green** card when it comes back, with how long it was down

A device isn't called offline the first time a scan misses it. **Offline after
missed scans** in the Settings tab (default 2) is how many consecutive scans of
its network must miss it first, so a laptop waking or a phone dropping Wi-Fi
for one sweep doesn't fire a red card and a green one a minute apart. It's
counted in scans rather than minutes, so it means the same thing whatever a
network's interval is; set it to 1 for the old first-miss-counts behaviour.

**Remove** clears it and turns alerts off.

Notes:

- The URL is stored in the database and **overrides `Bamf:WebhookUrl`** in
  `appsettings.json`, the same way every other Settings-tab value overrides its
  config setting. The config setting still works if you'd rather manage it that way.
- The dashboard only ever shows a **masked** form of the URL
  (`https://discord.com/api/webhooks/1234567890/AbC••••••••`). The token is the
  credential, and anyone who can load the dashboard could read it otherwise —
  so the full URL is never sent back to the browser.
- Non-Discord endpoints work too; they receive generic JSON with a `content`
  field instead of Discord's embed format.
- An `http://` URL is accepted but flagged, in the dialog and at startup:
  alerts would travel in plaintext.

## Backups

Every update snapshots `bamf.db` into `backups/` before touching anything. That
covers the risky moment, but it only fires when you update - go a month without
updating and your newest snapshot is a month old.

For a scheduled snapshot:

**Windows** - run once, elevated, from the `windows` folder:

```powershell
powershell -ExecutionPolicy Bypass -File Backup-BAMF.ps1 -Install
```

That registers a **BAMF Backup** scheduled task running nightly at 03:00 as
SYSTEM. Run it by hand any time with `Backup-BAMF.ps1`, or
`Start-ScheduledTask -TaskName "BAMF Backup"`.

**Linux** - `install.sh` sets this up for you: `bamf-backup.timer` runs nightly
at 03:00 (with `Persistent=true`, so a machine that was off catches up).

```bash
systemctl list-timers bamf-backup      # when it next runs
systemctl start bamf-backup.service    # run one now
systemctl disable --now bamf-backup.timer   # stop scheduled backups
```

Both keep the newest **30** snapshots and copy `appsettings.json` alongside
them, so a restore gets your subnets, webhook and password back too. At one a
night that's roughly a month of history; the database is small, so 30 costs
very little disk. Change it with `-Keep 60` or `BAMF_BACKUP_KEEP=60`.

The updaters prune to the same 30, because they write into the same folder — if
they kept fewer, running an update would trim your scheduled snapshots back.

### Why they stop the service

BAMF writes to `bamf.db` continuously. A plain copy taken mid-write can be
**torn** - SQLite may consider the result corrupt, and you would not find that
out until the moment you needed to restore it. Both scripts stop the service,
copy, and start it again, restarting even if the copy fails. BAMF misses at
most one scan cycle.

### Syncing backups to cloud storage

Point your sync client at the **`backups`** folder, never at `bamf.db` itself.
Snapshots are written once and never touched again, so they are safe to sync. A
live database is not: sync clients can capture a half-written file, and some
lock or replace files underneath the process holding them.

| | Windows | Linux |
|---|---|---|
| Safe to sync | `C:\BAMF\backups\` | `/opt/bamf/backups/` |
| Do **not** sync | `C:\BAMF\bamf.db` | `/opt/bamf/bamf.db` |

### Restoring

Stop the service, copy a snapshot over `bamf.db`, start it again - see
[Rolling back](#rolling-back).

## Feedback and bug reports

The **Feedback** link beside the version in the header, and **Tools ▾ →
Feedback / report a bug…**, both open a new GitHub issue with the version and
build date already filled in — the one fact every bug report needs and everyone
forgets.

It carries **nothing about your network**: no device names, addresses, MACs or
counts. Just the BAMF version, the build stamp, and your browser string.

The link follows `Bamf:UpdateRepo`, so a fork sends reports to its own tracker
rather than upstream's.

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

### Bandwidth history

The traffic monitor's counters used to start from zero each time BAMF
started. Now it writes bytes per device per hour to the database every five
minutes, kept for the history retention window. The History panel shows
**Traffic this week** as a bar per hour with the totals in and out, and the
scheduled report's top talkers are counted over the report's own period.
`GET /api/hosts/{id}/traffic` has the numbers.

### Latency and uptime

After each scan, BAMF pings every online device on the networks the scan
covered, once, and keeps the round-trip time. It's one small packet per device
per scan; switch it off under **Settings → Behaviour** (or `Bamf:LatencyProbe`)
if you'd rather it didn't.

- The **Latency** column shows each device's last round-trip time, green under
  20 ms, amber over 100 ms, red over 300 ms. A device that's online but didn't
  answer the ping says **no reply**: plenty of devices drop ICMP while working
  perfectly. Click the column heading to sort by it.
- A device's **History** panel has a chart of the last 24 hours, with the
  average and range, and a red tick wherever a ping went unanswered.
- **Uptime** sits above the chart: the share of the last 7 and 30 days the
  device was online, from the same online/offline history the timeline uses.
  It counts from when the device was first seen, so a week-old device's 30-day
  figure is over its own week, and it can only see as far back as the history
  retention window. "The printer was down 6% of the month" is the number to
  look for.

Samples age out on the same window as events (`HistoryRetentionDays`).

### Traffic, DHCP and DNS

With the Npcap driver installed (the same one active ARP uses), BAMF can watch
the wire, receive-only, and never sends a packet for it. Switch it under
**Settings → Behaviour** or with `Bamf:TrafficMonitor`; it's on by default and
does nothing without Npcap.

**Bytes per device.** Every frame's source and destination are counted, so
each device gets bytes in and out. The device list gains a **Traffic** column
with the rate over the last ten seconds (sortable), the History panel shows
totals and a five-minute strip, and the **Activity** tab opens with a **Top
talkers** card: the devices that moved the most bytes since the monitor
started, each with its strip.

What this machine can see depends on where it sits. On an ordinary switched
network it sees its own traffic plus broadcast and multicast, so the numbers
are "traffic with the BAMF server" rather than the whole network's. Plug the
server into a **mirrored (SPAN) port** on a managed switch, or an old hub,
and it sees everything. The card says so. The same goes for the DHCP and DNS
watch below: on a switched network it covers this machine and any broadcast
DHCP offers; on a mirrored port it covers every device.

**DHCP servers.** Every DHCP offer or acknowledgement names the server that
sent it. The **Network watch** card on the Activity tab lists every DHCP
server seen, with its MAC, how many offers, and when. A second DHCP server
appearing is the classic sign of a rogue router or a misconfigured box, so a
server not seen before is an **alert**: in the card, in the log, and to your
webhook. The first servers seen while BAMF has none on record are learned
quietly.

**DNS servers.** Every DNS query names the server the device asked. The card
lists every DNS server in use with how many devices ask it, and each device's
History panel says which it asks. Two things alert:

- a DNS server no device on the network had used before;
- a device that starts asking a server it never used before ("phone changed
  DNS server: it now asks 8.8.8.8; before it used 192.168.1.1").

Both are classic signs of a device whose settings were changed behind your
back. **Trust** a server in the card so it never alerts, or **Forget** one so
it counts as new again. Known servers are remembered across restarts, and each
alert fires at most once a day per server or device.

### The New tab

**New**, in the device list's status tabs, shows every device first seen in the
last 7 days, with a count on the tab while there are any. It's the weekly
check: anything here that you don't recognise wants a name, a note or a
closer look.

### Tags

**Tags…** in a device's ⋯ menu gives it any tags you like: "kids", "IoT",
"work", "guest". Type them separated by commas, or click a tag already in use
to add or remove it. Tags show as small chips under the device's name.

- A **Tag** chip bar appears above the device list once any device has a tag.
  Click a tag to see only those devices; the **Map** shows only them too, on
  every layout, with your switches still drawn.
- **Who's home** has a **Show** picker: your watched devices, as before, or
  every device carrying one tag, so the board can show "kids" one moment and
  "IoT" the next.
- Search matches tags as well.

### Export and import the layout

**Settings → Switches and routers** has **Export layout** and **Import
layout…**. Export downloads one JSON file with everything you recorded:

- switches, routers, access points, SSIDs, VPNs and virtual switches, what
  each is plugged into, and every port label;
- which port each device is on;
- each device's name, note, link, known/watched/ignored flags, type and tags;
- combined network cards, declared gateways, type icons, and where you dragged
  things on the Map.

Devices are keyed by **MAC** and switches by their place in the file, so the
file means the same thing on another server. Keep it as a backup, or use it
to move BAMF to a new machine: install, let it scan, then import.

**Import replaces the recorded layout wholesale**; the devices themselves and
their history aren't touched. A device in the file that this server hasn't
seen yet is skipped, and the import says so; import again once BAMF has seen
it and its settings are filled in. `GET /api/layout` and `POST /api/layout`
do the same from a script.

### MQTT and Home Assistant

Give BAMF an MQTT broker and every device becomes a presence entity in Home
Assistant, so automations can run when someone gets home or the last phone
leaves. Set it in `appsettings.json` (and only there, since the broker
password has no business in the dashboard), then restart:

```json
"Mqtt": {
  "Server": "homeassistant.local",
  "Port": 1883,
  "Username": "bamf",
  "Password": "…",
  "TopicPrefix": "bamf",
  "Discovery": true
}
```

- Each device (not ignored, not forgotten) gets a retained `home` or
  `not_home` on `bamf/<mac>/state` (the MAC without colons, lower case), and
  its details (`ip`, `name`, `vendor`, `network`, `last_seen`, `latency_ms`,
  `known`, `watched`) as JSON on `bamf/<mac>/attributes`. `bamf/status` says
  whether BAMF itself is connected, with a last will of `offline`.
- With **Discovery** on (the default), BAMF publishes Home Assistant's MQTT
  discovery config for each device, so each shows up as a `device_tracker`
  under **Settings → Devices & services → MQTT** with no YAML. Turn it off if
  you'd rather write your own.
- Presence is published within a few seconds of a scan, and only when it
  changed. A device you ignore, forget or delete is taken back off the broker.
- `Tls: true` for a broker on 8883. The **Set in appsettings.json** card on the
  Settings tab shows whether BAMF is connected and how much it has published.

BAMF speaks MQTT 3.1.1 itself (connect, publish, ping), so there's no extra
dependency, and it never subscribes to anything.

### One device, several addresses

A router usually has an address on every network it routes, and a server can
have a second IP. That's one MAC answering on several addresses at once, and
BAMF keeps them all:

- The device keeps one **main address**, the one it was first seen at. It stays
  put while it still answers, so the device no longer flips between addresses
  on every scan.
- Its other addresses show as a **+N** chip beside the IP. Click it for the
  full list: each address, its network, and when it last answered. Each one
  opens the device's link on that address.
- The device is listed on **every network tab** it has an address on, and drawn
  on each network's **Map** at its address there. Search finds it by any of
  its addresses.
- Its **History** panel lists the addresses it answers on now.
- An address stops counting after as many missed scans as it takes a device to
  go offline (`Bamf:OfflineAfterMissedScans`). Only then does the main address
  move to one that still answers, and only that is recorded as a change of
  address. An ordinary DHCP move looks the same: the new address appears, the
  old one stops answering, and the device moves.
- Six or more addresses on one MAC usually means **proxy ARP**, a router
  answering on behalf of other devices, and the list says so.

### One device, several network cards

A machine with a network card on each of your networks is a different case.
Each card has its own MAC, so BAMF sees one device per card. BAMF's own server
is the usual example: it has a card on every network it scans. **Network
cards…** in a device's ⋯ menu combines them into one device:

- Pick the device that's another card of this one and click **Combine**.
  BAMF's own other cards, and devices with the same name, are suggested first.
- The combined device answers on every card's addresses, with the card's MAC
  shown beside each address that isn't its own. It's online if any card is. It
  has one row in the device list.
- On the **Map** it's drawn once. For BAMF that means once, under the switch
  it's plugged into, on every network's map.
- Where the card was plugged in and what it was declared the gateway of pass to
  the device it joins. A card that is a switch's or router's own device can't
  be combined into another; combine the other way round.
- **Separate** in the same dialog makes a card a device of its own again. The
  cards' own records are kept as they were, so nothing is lost.

## Limitations to be aware of

- ARP only sees the L2 segment the server sits on. Hosts on other VLANs are
  invisible unless the server has an interface on that segment and it's listed
  in `Subnets`. The dashboard's network switcher shows each configured network
  separately.
- Hostnames come from reverse DNS first, then a NetBIOS query (UDP 137) as a
  fallback - this names many Windows PCs, NAS boxes, and printers that have no
  DNS record. Devices that answer neither may still name themselves over mDNS
  (below). Anything left still shows "-"; name those by hand, and the name
  sticks to the MAC.
- **mDNS listening** (on by default; switch it in the Settings tab or with
  `Bamf:MdnsListen`) joins the multicast group on each local network and reads
  the announcements devices make about themselves - Apple TVs, Chromecasts,
  Sonos speakers, printers, HomeKit and Matter gear. BAMF never sends an mDNS
  query: the only packet this causes is the kernel's own multicast membership
  report, which any listener has to make. A name learned this way is shown only
  when the device has no DNS or NetBIOS name, and the announced services refine
  the device guess. Because BAMF only listens, coverage grows with time - a
  device is named when it next announces itself or answers someone else's
  query, not on the first scan. Port 5353 is shared with the operating system's
  own responder; if it can't be shared, the Settings tab says so and everything
  else carries on.
- Phones with MAC randomization appear as new "(randomized MAC)" hosts each
  time they rejoin. With auto-ignore enabled (default; toggle under
  **Settings → Behaviour** or via `AutoIgnoreRandomizedMacs`), these are auto-filed
  under the Ignored tab and never alert. You can also manually
  Ignore/Unignore any host from the dashboard.
- In ping-sweep mode, hosts that neither answer ping nor talk on the network
  during a cycle may briefly show offline even if powered on. Enable
  `ActiveArpScan` + install Npcap to catch these.
