#!/bin/bash
# BAMF installer/updater for Debian-based systems (Proxmox LXC containers).
# Run as root from inside the extracted BAMF source folder:
#   bash linux/install.sh
# Re-running the same script performs an update: it preserves your
# appsettings.json and database, and snapshots the DB into /opt/bamf/backups.

set -euo pipefail

APP_DIR="/opt/bamf"
TMP_SRC=""

# Source resolution:
#   bash install.sh /path/to/BAMF.zip   -> extract zip to temp, build, clean up
#   bash install.sh                      -> if run from inside the source tree, use it;
#                                           else use newest BAMF*.zip in cwd or /root
resolve_source() {
    local zip="${1:-}"
    if [ -z "$zip" ]; then
        local here; here="$(cd "$(dirname "$0")/.." 2>/dev/null && pwd)"
        if [ -f "$here/BAMF.csproj" ]; then SRC_DIR="$here"; return; fi
        zip="$(ls -1t ./BAMF*.zip /root/BAMF*.zip 2>/dev/null | head -1 || true)"
        [ -n "$zip" ] || { echo "No source tree or BAMF*.zip found. Pass the zip: bash install.sh /path/to/BAMF.zip"; exit 1; }
    fi
    [ -f "$zip" ] || { echo "Zip not found: $zip"; exit 1; }
    command -v unzip >/dev/null || { apt-get update -qq; apt-get install -y -qq unzip >/dev/null; }
    TMP_SRC="$(mktemp -d)"
    echo -e "\e[36m==> Extracting $zip (temporary)\e[0m"
    unzip -q "$zip" -d "$TMP_SRC"
    SRC_DIR="$(dirname "$(find "$TMP_SRC" -name BAMF.csproj | head -1)")"
    [ -f "$SRC_DIR/BAMF.csproj" ] || { echo "BAMF.csproj not found in zip."; exit 1; }
}
cleanup() { [ -n "$TMP_SRC" ] && rm -rf "$TMP_SRC"; }
trap cleanup EXIT

step() { echo -e "\e[36m==> $*\e[0m"; }
[ "$(id -u)" -eq 0 ] || { echo "Run as root (inside the container)."; exit 1; }

resolve_source "${1:-}"
DOTNET_DIR="/opt/dotnet"
UNIT_SRC="$SRC_DIR/linux/bamf.service"  # resolved after resolve_source
UNIT_DST="/etc/systemd/system/bamf.service"

# --- follow an existing install ---
# The unit may point somewhere other than /opt/bamf. Building into the default
# while the service runs from elsewhere changes nothing and still reports
# success, so update whatever folder is actually in use - which also keeps that
# install's appsettings.json and bamf.db.
if [ -f "$UNIT_DST" ]; then
    EXISTING_EXEC="$(sed -n 's/^ExecStart=//p' "$UNIT_DST" | head -1 | awk '{print $1}')"
    if [ -n "$EXISTING_EXEC" ]; then
        EXISTING_DIR="$(dirname "$EXISTING_EXEC")"
        if [ -d "$EXISTING_DIR" ] && [ "$EXISTING_DIR" != "$APP_DIR" ]; then
            step "Service runs from $EXISTING_DIR - updating that folder (keeps its config and database)"
            APP_DIR="$EXISTING_DIR"
        fi
    fi
fi

# --- dependencies ---
step "Installing dependencies (libpcap, curl, ca-certificates)"
apt-get update -qq
apt-get install -y -qq libpcap0.8 curl ca-certificates >/dev/null

# --- .NET SDK (distro-agnostic install, works on any Debian version) ---
if [ ! -x "$DOTNET_DIR/dotnet" ]; then
    step "Installing .NET 8 SDK to $DOTNET_DIR (one-time, ~200 MB)"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --channel 8.0 --install-dir "$DOTNET_DIR"
fi
export DOTNET_ROOT="$DOTNET_DIR"
export PATH="$DOTNET_DIR:$PATH"

# --- stop if running ---
if systemctl is-active --quiet bamf 2>/dev/null; then
    step "Stopping bamf service"
    systemctl stop bamf
fi

# --- preserve config + snapshot database ---
CONFIG_BAK=""
if [ -f "$APP_DIR/appsettings.json" ]; then
    CONFIG_BAK="$(mktemp)"
    cp "$APP_DIR/appsettings.json" "$CONFIG_BAK"
    step "Preserving existing appsettings.json"
fi
if [ -f "$APP_DIR/bamf.db" ]; then
    mkdir -p "$APP_DIR/backups"
    STAMP="$(date +%Y%m%d-%H%M%S)"
    cp "$APP_DIR/bamf.db" "$APP_DIR/backups/bamf-$STAMP.db"
    step "Database backed up to backups/bamf-$STAMP.db"
    ls -1t "$APP_DIR/backups"/bamf-*.db 2>/dev/null |  tail -n +31 | xargs -r rm -f
fi

# --- build ---
step "Building (first build takes a few minutes)"
cd "$SRC_DIR"
dotnet publish -c Release -r linux-x64 -p:PublishSingleFile=true --self-contained true -o "$APP_DIR"

# --- restore config ---
if [ -n "$CONFIG_BAK" ]; then
    cp "$APP_DIR/appsettings.json" "$APP_DIR/appsettings.new.json"
    cp "$CONFIG_BAK" "$APP_DIR/appsettings.json"
    rm -f "$CONFIG_BAK"
    step "Restored your appsettings.json (new defaults saved as appsettings.new.json)"
fi

# --- keep the installer inside the app folder for future updates ---
cp "$SRC_DIR/linux/install.sh" "$APP_DIR/install.sh"
chmod +x "$APP_DIR/install.sh"

# --- prove the build produced what we expect ---
[ -x "$APP_DIR/BAMF" ] || { echo "Build finished but $APP_DIR/BAMF is missing - nothing was installed."; exit 1; }
[ -f "$APP_DIR/wwwroot/index.html" ] || { echo "Build finished but $APP_DIR/wwwroot is missing - the dashboard would not load."; exit 1; }

# --- service ---
step "Installing systemd unit"
if [ "$APP_DIR" = "/opt/bamf" ]; then
    cp "$UNIT_SRC" "$UNIT_DST"
else
    # keep the unit pointing at the folder we actually installed to
    sed "s#/opt/bamf#$APP_DIR#g" "$UNIT_SRC" > "$UNIT_DST"
fi
# --- nightly backup timer ---
# Snapshots the database on a schedule, not only when you update. Stops the
# service for the moment it takes to copy, so the snapshot is consistent.
cp "$SRC_DIR/linux/bamf-backup.sh" "$APP_DIR/bamf-backup.sh"
chmod +x "$APP_DIR/bamf-backup.sh"
for u in bamf-backup.service bamf-backup.timer; do
    if [ "$APP_DIR" = "/opt/bamf" ]; then
        cp "$SRC_DIR/linux/$u" "/etc/systemd/system/$u"
    else
        sed "s#/opt/bamf#$APP_DIR#g" "$SRC_DIR/linux/$u" > "/etc/systemd/system/$u"
    fi
done

systemctl daemon-reload
systemctl enable bamf >/dev/null 2>&1
systemctl enable --now bamf-backup.timer >/dev/null 2>&1
step "Nightly backup timer enabled (03:00; systemctl disable --now bamf-backup.timer to stop)"
step "Starting bamf"
systemctl start bamf

VER="$(sed -n 's:.*<Version>[[:space:]]*\([^<[:space:]]*\)[[:space:]]*</Version>.*:\1:p' "$SRC_DIR/BAMF.csproj" | head -1)"

echo
echo -e "\e[32mBAMF ${VER:-} is running from $APP_DIR.\e[0m"
IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
echo "Dashboard: http://${IP:-<container-ip>}:8840"
[ -n "$VER" ] && echo "Check the dashboard header shows v$VER - if it doesn't, you're seeing a cached page (Ctrl+F5)."
echo "Logs:      journalctl -u bamf -f"
echo "Update:    copy the new zip in and run: bash /opt/bamf/install.sh /root/BAMF.zip"
