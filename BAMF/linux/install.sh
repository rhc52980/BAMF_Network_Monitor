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
    ls -1t "$APP_DIR/backups"/bamf-*.db 2>/dev/null | tail -n +11 | xargs -r rm -f
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

# --- service ---
step "Installing systemd unit"
cp "$UNIT_SRC" "$UNIT_DST"
systemctl daemon-reload
systemctl enable bamf >/dev/null 2>&1
step "Starting bamf"
systemctl start bamf

echo
echo -e "\e[32mBAMF is running.\e[0m"
IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
echo "Dashboard: http://${IP:-<container-ip>}:8840"
echo "Logs:      journalctl -u bamf -f"
echo "Update:    copy the new zip in and run: bash /opt/bamf/install.sh /root/BAMF.zip"
