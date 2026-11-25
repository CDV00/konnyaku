#!/usr/bin/env bash
# Runs a command of the backup services on this server, e.g.:
#   ./scripts/kk-backup.sh info                       backups kept, WAL range (earliest/latest restorable point)
#   ./scripts/kk-backup.sh backup full                a backup now (full | diff | incr)
#   ./scripts/kk-backup.sh drill                      restore drill into a throw-away PostgreSQL, compares row counts
#   ./scripts/kk-backup.sh drill --time "2026-11-25 10:30:00+07" --keep
#                                                     state at that moment kept running, to copy data out of it
#   ./scripts/kk-backup.sh status                     refresh and print backups/status/backup.json
# Files of the Content module (bucket of Garage or the provider; service files-backup, rclone):
#   ./scripts/kk-backup.sh files backup               off-site copy of the bucket now
#   ./scripts/kk-backup.sh files restore [YYYY-MM-DD] copy the backup back into the bucket (newer files are kept); with a
#                                                     date, also the versions that day's run removed or replaced
#   ./scripts/kk-backup.sh files status               print backups/status/files.json
# Restoring the production database itself: scripts/restore.sh. Runbook: docs/trien-khai.md#backup
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ "${1:-}" == files ]]; then
  shift
  exec docker compose exec files-backup /bin/sh /opt/konnyaku/files-backup.sh "$@"
fi
exec docker compose exec backup /opt/konnyaku/bin/kk-backup "$@"
