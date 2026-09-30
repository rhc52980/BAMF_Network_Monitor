# BAMF

BAMF, the Basic ARP Monitoring Framework, watches your network: every device,
when it comes and goes, what it is, and what changed. This add-on runs the
same BAMF as everywhere else, next to Home Assistant.

## Install

1. In Home Assistant, go to **Settings → Add-ons → Add-on Store**, open the
   **⋮** menu, choose **Repositories**, and add
   `https://github.com/rhc52980/BAMF_Network_Monitor`.
2. Find **BAMF** in the store and install it.
3. On the **Configuration** tab, list your networks, or leave the list empty
   to scan every network the Home Assistant host is on.
4. Start it, and use **Open Web UI**. The dashboard is on port 8840. The first
   time it opens it asks which networks to watch and for a password; with a
   **Dashboard password** set here, it asks for that one first and doesn't ask
   for another.

## Options

| Option | What it does |
|---|---|
| Networks | Networks to scan, like `192.168.1.0/24`. Empty scans every network the host is on. |
| Scan every | Seconds between scans of each network. |
| Dashboard password | Asks for this before showing the dashboard. |
| View-only password | With a dashboard password set, a second password that can look at everything but change nothing. |
| Reset a forgotten password | Switch on and restart to clear the passwords set in BAMF's own Settings. Then switch it off, and set a new one in Settings → Security. |
| Webhook | Where alerts go: Discord, ntfy, Gotify, or any URL that takes a JSON POST. |
| Active ARP scanning | Finds devices whose firewalls ignore ping. |
| Traffic monitor | Bytes per device, the DHCP and DNS servers in use, and the ARP and IPv6 watches between scans. |
| MQTT broker, username, password | Publishes every device's presence to Home Assistant through MQTT discovery. |

The options are where BAMF starts. Anything you change in BAMF's own
**Settings** afterwards is kept and wins over them, the same as it does over
`appsettings.json`.

## MQTT and Home Assistant

With the Mosquitto broker add-on installed, set **MQTT broker** to your Home
Assistant's own address (not `core-mosquitto`: BAMF runs on the host's
network, where that name doesn't resolve) and give it a Mosquitto user.
Every device then shows up in Home Assistant as a presence sensor, and a
device called BAMF carries sensors for the network as a whole: devices online,
unknown devices (with their names), the latest speed test, and, with BAMF's
internet watch on, whether the internet is up. The broker can also be set, or
changed, in BAMF's **Settings → System**.

## What it needs

- **The host's network.** ARP only sees the network the process is actually
  on, so the add-on runs with host networking. Its dashboard is on port 8840
  of your Home Assistant.
- **Raw network access** (`NET_RAW`, `NET_ADMIN`) for the active ARP scan and
  the traffic monitor. Without them, BAMF still works on ping sweeps.

Its database lives in the add-on's own storage, so it survives updates and is
included in Home Assistant backups. BAMF's own nightly backups are off in the
add-on for that reason; switch them on under **Settings → System** if you want
both.

## More

Everything else BAMF does, from the Map to the security watch, is in the
[README](https://github.com/rhc52980/BAMF_Network_Monitor/blob/main/BAMF/README.md).
