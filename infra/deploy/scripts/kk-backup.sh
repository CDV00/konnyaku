#!/usr/bin/env bash
# Runs a command of the backup service (pgBackRest) on this server, e.g.:
#   ./scripts/kk-backup.sh info                       backups kept, WAL range (earliest/latest restorable point)
#   ./scripts/kk-backup.sh backup full                a backup now (full | diff | incr)
#   ./scripts/kk-backup.sh drill                      restore drill into a throw-away PostgreSQL, compares row counts
#   ./scripts/kk-backup.sh drill --time "2026-11-25 10:30:00+07" --keep
#                                                     state at that moment kept running, to copy data out of it
#   ./scripts/kk-backup.sh status                     refresh and print backups/status/backup.json
# Restoring the production database itself: scripts/restore.sh. Runbook: docs/trien-khai.md#backup
set -euo pipefail
cd "$(dirname "$0")/.."
exec docker compose exec backup /opt/konnyaku/bin/kk-backup "$@"
