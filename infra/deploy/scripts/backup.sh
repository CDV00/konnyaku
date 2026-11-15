#!/usr/bin/env bash
# Nightly logical backup of the whole database (all module schemas), run from cron on the server:
#   15 2 * * * cd /opt/konnyaku && ./scripts/backup.sh >> backups/backup.log 2>&1
# Writes BACKUP_DIR/konnyaku-<UTC timestamp>.dump (pg_dump custom format), keeps BACKUP_KEEP_DAYS days locally.
# Off-site copy: if rclone is installed and RCLONE_REMOTE is set (e.g. "b2:konnyaku-backups"), the dump is uploaded.
# Restore drill: docs/trien-khai.md#khôi-phục
set -euo pipefail
cd "$(dirname "$0")/.."

set -a
# shellcheck disable=SC1091
source .env
set +a

dir="${BACKUP_DIR:-./backups}"
keep="${BACKUP_KEEP_DAYS:-14}"
name="konnyaku-$(date -u +%Y%m%dT%H%M%SZ).dump"
mkdir -p "$dir"

# Dump inside the container to the mounted /backups; the ledger and every schema are included.
docker compose exec -T postgres pg_dump -U konnyaku -d konnyaku -Fc -Z 6 -f "/backups/$name.partial"
mv "$dir/$name.partial" "$dir/$name"

# A dump that cannot be listed is not a backup.
docker compose exec -T postgres pg_restore --list "/backups/$name" > /dev/null

if [[ -n "${RCLONE_REMOTE:-}" ]] && command -v rclone > /dev/null; then
  rclone copy "$dir/$name" "$RCLONE_REMOTE"
fi

find "$dir" -name 'konnyaku-*.dump' -mtime +"$keep" -delete
echo "$(date -u +%FT%TZ) ok $name $(du -h "$dir/$name" | cut -f1)"
