# BAMF on Proxmox (LXC container)

BAMF runs natively in a Debian LXC container - same features, same dashboard,
same database format as the Windows build.

## 1. Create the container

On the Proxmox web UI (or shell), create an LXC from a current **Debian**
template (bookworm or newer):

- **Unprivileged: yes** is fine to start - everything works, and active ARP
  scanning usually works too. If active ARP falls back to ping sweep (check
  the "Last scan" card), recreate as a **privileged** container.
- Resources: 1 CPU core, 512 MB RAM, 4 GB disk is plenty.
- **Network**: this is the important part. The container can only scan
  networks it has a leg on:
  - `net0` → bridge for your first network (e.g. `vmbr0`, 192.168.1.0/24)
  - Add `net1` → bridge/VLAN for your second network (e.g. 192.168.2.0/24)
    via container → Network → Add.
  - DHCP is fine, static is nicer for a monitoring box.

Shell example:

```bash
pct create 200 local:vztmpl/debian-12-standard_12.7-1_amd64.tar.zst \
  --hostname bamf --memory 512 --cores 1 --rootfs local-lvm:4 \
  --net0 name=eth0,bridge=vmbr0,ip=dhcp \
  --net1 name=eth1,bridge=vmbr1,ip=dhcp \
  --unprivileged 1 --features nesting=1 --start 1
```

## 2. Install BAMF

Copy `BAMF.zip` into the container and run the installer:

```bash
# from the Proxmox host:
pct push 200 BAMF.zip /root/BAMF.zip

# inside the container (pct enter 200) - first time, extract just the script:
cd /root && apt-get update && apt-get install -y unzip
unzip -j BAMF.zip "BAMF/linux/install.sh" -d /root
bash /root/install.sh /root/BAMF.zip
```

The script extracts the zip to a temp folder, builds to `/opt/bamf`, and cleans
up - `/opt/bamf` is the only folder that persists, holding the app, your
config, database, and backups.

The script installs libpcap + the .NET SDK (one-time), builds to `/opt/bamf`,
installs a systemd service, and starts it. Then open
`http://<container-ip>:8840`.

## 3. Configure

Edit `/opt/bamf/appsettings.json` (set your `Subnets`, `Password`, webhook),
then `systemctl restart bamf`. Runtime toggles (active ARP, auto-ignore) work
from the dashboard as usual.

## 4. Updating

Push the new zip in and run the installer copy that lives in `/opt/bamf` - it
preserves your config + database (with a snapshot in `/opt/bamf/backups`):

```bash
# host:      pct push 200 BAMF.zip /root/BAMF.zip
# container: bash /opt/bamf/install.sh /root/BAMF.zip
```

## Notes

- **Active ARP in LXC**: SharpPcap uses libpcap (installed by the script). The
  systemd unit grants `CAP_NET_RAW`/`CAP_NET_ADMIN`. In unprivileged
  containers this normally suffices; if a scan logs a pcap failure, BAMF falls
  back to ping sweep automatically - switch the container to privileged for
  full active ARP.
- **Logs**: `journalctl -u bamf -f`
- **What it can see**: same rule as anywhere - ARP is layer 2, so BAMF sees
  the segments its interfaces sit on. One `netX` interface per network you
  want watched.
- The `update/` folder (Windows updater + desktop shortcut) is not used on
  Linux; `linux/install.sh` covers install + update.
