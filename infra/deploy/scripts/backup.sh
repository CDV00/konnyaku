#!/usr/bin/env bash
# Second backup layer: nightly logical dump of the whole database (all module schemas), run from cron on the server:
#   15 2 * * * cd /opt/konnyaku && ./scripts/backup.sh >> backups/backup.log 2>&1
# The main backup is continuous (pgBackRest, service `backup`, docs/trien-khai.md#backup). This dump does not depend on
# it (other tool and format, no encryption key, one schema or table can be restored from it).
# Writes BACKUP_DIR/konnyaku-<UTC timestamp>.dump (pg_dump custom format), keeps BACKUP_KEEP_DAYS days locally, and
# BACKUP_DIR/status/dump.prom + dump.json for monitoring (docs/trien-khai.md, "File trạng thái").
# Off-site copy: if rclone is installed and RCLONE_REMOTE is set (e.g. "b2:konnyaku-backups"), the dump is uploaded.
set -euo pipefail
cd "$(dirname "$0")/.."

set -a
# shellcheck disable=SC1091
source .env
set +a

dir="${BACKUP_DIR:-./backups}"
keep="${BACKUP_KEEP_DAYS:-7}"
name="konnyaku-$(date -u +%Y%m%dT%H%M%SZ).dump"
mkdir -p "$dir/status"

# Dump inside the container to the mounted /backups; the ledger and every schema are included.
docker compose exec -T postgres pg_dump -U konnyaku -d konnyaku -Fc -Z 6 -f "/backups/$name.partial"
mv "$dir/$name.partial" "$dir/$name"

# A dump that cannot be listed is not a backup.
docker compose exec -T postgres pg_restore --list "/backups/$name" > /dev/null

if [[ -n "${RCLONE_REMOTE:-}" ]] && command -v rclone > /dev/null; then
  rclone copy "$dir/$name" "$RCLONE_REMOTE"
fi

find "$dir" -name 'konnyaku-*.dump' -mtime +"$keep" -delete

# Status for monitoring, written only on success: an old timestamp means the dump is failing.
now=$(date -u +%s)
size=$(stat -c %s "$dir/$name")
{
  echo "# HELP konnyaku_dump_last_success_timestamp_seconds Last successful nightly pg_dump (scripts/backup.sh)."
  echo "# TYPE konnyaku_dump_last_success_timestamp_seconds gauge"
  echo "konnyaku_dump_last_success_timestamp_seconds $now"
  echo "# HELP konnyaku_dump_last_size_bytes Size of the last dump."
  echo "# TYPE konnyaku_dump_last_size_bytes gauge"
  echo "konnyaku_dump_last_size_bytes $size"
} > "$dir/status/.dump.prom.tmp"
mv "$dir/status/.dump.prom.tmp" "$dir/status/dump.prom"
printf '{"last_success":"%s","file":"%s","size_bytes":%s}\n' "$(date -u -d "@$now" +%FT%TZ)" "$name" "$size" > "$dir/status/.dump.json.tmp"
mv "$dir/status/.dump.json.tmp" "$dir/status/dump.json"
echo "$(date -u +%FT%TZ) ok $name $(du -h "$dir/$name" | cut -f1)"
