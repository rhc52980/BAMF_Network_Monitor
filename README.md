# BAMF — Basic ARP Monitoring Framework

**Know every device on your network, and know the moment a new one appears.**

BAMF watches your LAN at layer 2. It discovers every device via ARP — including
the ones that ignore ping — remembers each MAC it has ever seen, and tells you
when something new turns up or something you care about drops off. It runs as a
Windows Service or a systemd unit, keeps everything in a single SQLite file, and
serves a dashboard on port 8840.

[![Latest release](https://img.shields.io/github/v/release/rhc52980/BAMF_Network_Monitor)](https://github.com/rhc52980/BAMF_Network_Monitor/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/rhc52980/BAMF_Network_Monitor/total)](https://github.com/rhc52980/BAMF_Network_Monitor/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
![Platforms](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-lightgrey)

---

## Why it exists

Most tools in this space are Docker- and Linux-first. BAMF runs **natively on
Windows** as a proper service — no container, no VM, no Linux box — while
working just as well in a Debian LXC on Proxmox. Same codebase, same database
format, same dashboard on both.

It also stays out of your business: **no cloud, no account, no telemetry**. The
dashboard has no external dependencies at all — fonts are served locally, there
are no CDN scripts and no analytics. The only connections BAMF makes are
scanning your own subnets, an optional one-time vendor-list download, an
optional daily update check you have to switch on, and your own webhook.

## What it does

**Discovery**
- ARP-based scanning that finds devices whose firewalls drop ICMP
- Optional raw ARP mode (Npcap/libpcap) with automatic fallback to a sweep
- Multiple subnets at once, scanning each from the right interface
- Hostnames via reverse DNS, then NetBIOS — which names most Windows PCs, NAS
  boxes and printers that have no DNS record
- Vendor names from the full IEEE OUI registry

**Knowing what things are**
- Automatic device guesses from vendor and hostname — "Google/Nest device",
  "Printer", "iPhone" — costing nothing and sending nothing
- On-demand **Identify**: one ICMP echo for the TTL plus a probe of eleven
  telling ports, producing guesses that name their own evidence, like
  `Windows (TTL 128, SMB)`
- Custom names and free-text notes, bound to the MAC so they survive IP changes

**Alerting**
- Discord webhooks with rich embeds — amber for a new unknown device, red when
  a watched device goes offline, green when it returns with how long it was down
- Set it up by pasting a URL into the dashboard; no config file, no restart
- Star only the devices you actually care about
- Auto-ignore phones using MAC randomisation, so they don't cry wolf

**Investigating**
- Per-host and network-wide port scanning, always on demand, never automatic
- Wildcard targets: `*.245` checks that address on every network,
  `192.168.2.*` walks a subnet
- Per-device links, so a device's IP opens its actual admin UI —
  `8006` for Proxmox, `https://{ip}:8443`, whatever it happens to be
- Session history per device, a 24-hour sparkline, and a network-wide activity feed

**Operating it**
- Version and build date in the header, the log and the API, so "which build is
  this?" is always answerable
- One script installs *and* updates, preserving your config and database
- Scheduled nightly backups keeping 30 snapshots, safe to sync to cloud storage
- Optional password (HTTP Basic), optional HTTPS
- 17 themes, a mobile card layout, and a comic-book splat when you switch them

**For scripts and AI agents**
- `GET /api/hosts.txt` returns the whole device table as plain text — no JSON,
  no markup, sorted so two fetches diff cleanly
- Every route that changes anything is a POST or DELETE, so a consumer limited
  to that one GET is inherently read-only

## Install

Download a release — **no .NET SDK needed, the runtime is bundled**:

| | |
|---|---|
| **Windows** | [`BAMF-*-win-x64.zip`](https://github.com/rhc52980/BAMF_Network_Monitor/releases/latest) |
| **Linux** | [`BAMF-*-linux-x64.tar.gz`](https://github.com/rhc52980/BAMF_Network_Monitor/releases/latest) |

Extract, run the binary, open `http://localhost:8840`, and set your subnets in
`appsettings.json`.

Prefer the installer to manage the service, updates and backups for you? Build
from source instead — `windows\Install-BAMF.bat` or `linux/install.sh`, both of
which install *and* update. That route needs the .NET 8 SDK on the machine
(Linux fetches it for you).

**Full documentation:** [`BAMF/README.md`](BAMF/README.md) ·
**Proxmox / LXC guide:** [`BAMF/linux/README-PROXMOX.md`](BAMF/linux/README-PROXMOX.md)

## What it can't do

ARP is layer 2, so BAMF only sees segments it has an interface on. Devices on
another VLAN are invisible unless the machine has a leg on that network too —
one NIC per subnet you want watched.

It's also a monitor, not a security product. Device identification is a
heuristic, not nmap: no stack fingerprinting, no version detection. It reports
what the evidence suggests and names the evidence, so you can judge it.

## Requirements

- **Windows** Server 2022, or Windows 10/11 · **Linux** Debian 12 / Ubuntu 22.04+
- Optional, for raw ARP scanning: [Npcap](https://npcap.com) on Windows,
  `libpcap` on Linux — without it BAMF falls back to a sweep
- A network interface on each subnet you want to watch

## Licence

MIT — see [LICENSE](LICENSE).
