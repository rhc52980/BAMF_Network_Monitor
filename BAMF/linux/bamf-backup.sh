#!/bin/bash
# BAMF scheduled backup. Installed to /opt/bamf and run by bamf-backup.timer.
#
#   bash bamf-backup.sh [app-dir]        default: /opt/bamf
#   BAMF_BACKUP_KEEP=20 bash bamf-backup.sh
#
# Takes a consistent snapshot of bamf.db and appsettings.json into <app>/backups,
# then prunes to the newest $KEEP snapshots.
#
# The service is stopped for the moment it takes to copy. bamf.db is written
# continuously, and a copy taken mid-write can be torn - SQLite may consider the
# result corrupt, and you would not find out until you tried to restore it.
# BAMF misses at most one scan cycle.

set -euo pipefail

APP_DIR="${1:-/opt/bamf}"
KEEP="${BAMF_BACKUP_KEEP:-30}"
DB="$APP_DIR/bamf.db"
BAK="$APP_DIR/backups"

step() { echo -e "\e[36m==> $*\e[0m"; }

[ -f "$DB" ] || { echo "No database at $DB - is this the right folder?"; exit 1; }
mkdir -p "$BAK"

WAS_RUNNING=0
if systemctl is-active --quiet bamf 2>/dev/null; then
    WAS_RUNNING=1
    step "Stopping bamf for a consistent copy"
    systemctl stop bamf
fi

# Always restart, even if the copy fails.
restart_if_needed() { [ "$WAS_RUNNING" -eq 1 ] && systemctl start bamf && step "Started bamf"; }
trap restart_if_needed EXIT

STAMP="$(date +%Y%m%d-%H%M%S)"
cp "$DB" "$BAK/bamf-$STAMP.db"
step "Snapshot: backups/bamf-$STAMP.db"

# Config travels with it - it holds your subnets, webhook and password.
[ -f "$APP_DIR/appsettings.json" ] && cp "$APP_DIR/appsettings.json" "$BAK/appsettings.json"

# Keep the newest $KEEP, same retention the installer uses.
PRUNED="$(ls -1t "$BAK"/bamf-*.db 2>/dev/null | tail -n +$((KEEP + 1)) || true)"
if [ -n "$PRUNED" ]; then
    echo "$PRUNED" | xargs -r rm -f
    step "Pruned $(echo "$PRUNED" | wc -l) old snapshot(s), keeping $KEEP"
fi

echo
echo -e "\e[32mBackup complete: $BAK\e[0m"
