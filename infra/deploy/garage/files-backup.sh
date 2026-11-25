#!/bin/sh
# Off-site copy of the files bucket (service `files-backup`, image rclone/rclone; busybox sh).
# Source: the bucket of the Content module (Garage, or the provider's S3). Destination: the backup object storage of
# pgBackRest (BACKUP_S3_*), folder BACKUP_S3_PATH/files, encrypted client-side (rclone crypt, key from
# BACKUP_CIPHER_PASS: names and contents are unreadable to the storage provider).
#   current/                     the bucket as of the last run
#   history/YYYY-MM-DD/          files deleted or replaced by the run of that day, kept BACKUP_RETENTION_DAYS days
#
#   files-backup.sh daemon                 daily at BACKUP_TIME_UTC (UTC), plus a heartbeat in the status file
#   files-backup.sh backup                 one copy now
#   files-backup.sh restore [YYYY-MM-DD]   copy current/ back into the bucket (never deletes newer files); with a date,
#                                          then also what that day's run removed or replaced (older versions)
#   files-backup.sh status                 print the status file
# Status for monitoring: /backups/status/files.prom and files.json (docs/trien-khai.md, "File trạng thái").
set -u

STATUS_DIR="${STATUS_DIR:-/backups/status}"
STATE_FILE="$STATUS_DIR/.files-backup-state"

log() { echo "$(date -u +%FT%TZ) $*"; }
configured() { [ -n "${BACKUP_S3_BUCKET:-}" ]; }

# rclone remotes from the environment: src (bucket), offsite (backup storage), vault (encrypted folder in offsite).
export RCLONE_CONFIG_SRC_TYPE=s3 RCLONE_CONFIG_SRC_PROVIDER=Other RCLONE_CONFIG_SRC_ENDPOINT="$FILES_SOURCE_ENDPOINT" \
  RCLONE_CONFIG_SRC_REGION="${FILES_SOURCE_REGION:-garage}" RCLONE_CONFIG_SRC_ACCESS_KEY_ID="$FILES_SOURCE_KEY" \
  RCLONE_CONFIG_SRC_SECRET_ACCESS_KEY="$FILES_SOURCE_SECRET" RCLONE_CONFIG_SRC_FORCE_PATH_STYLE=true
# Same settings as pgBackRest (host name, HTTPS); FILES_OFFSITE_ENDPOINT (a full URL) overrides it for tests.
offsite_endpoint="https://${BACKUP_S3_ENDPOINT:-none}"
[ "${BACKUP_S3_PORT:-443}" = 443 ] || offsite_endpoint="$offsite_endpoint:${BACKUP_S3_PORT}"
offsite_endpoint="${FILES_OFFSITE_ENDPOINT:-$offsite_endpoint}"
path_style=true
[ "${BACKUP_S3_URI_STYLE:-path}" = host ] && path_style=false
export RCLONE_CONFIG_OFFSITE_TYPE=s3 RCLONE_CONFIG_OFFSITE_PROVIDER=Other RCLONE_CONFIG_OFFSITE_ENDPOINT="$offsite_endpoint" \
  RCLONE_CONFIG_OFFSITE_REGION="${BACKUP_S3_REGION:-auto}" RCLONE_CONFIG_OFFSITE_ACCESS_KEY_ID="${BACKUP_S3_KEY:-}" \
  RCLONE_CONFIG_OFFSITE_SECRET_ACCESS_KEY="${BACKUP_S3_KEY_SECRET:-}" RCLONE_CONFIG_OFFSITE_FORCE_PATH_STYLE="$path_style" \
  RCLONE_CONFIG_OFFSITE_NO_CHECK_BUCKET=true
export RCLONE_CONFIG_VAULT_TYPE=crypt RCLONE_CONFIG_VAULT_REMOTE="offsite:${BACKUP_S3_BUCKET:-none}${BACKUP_S3_PATH:-/konnyaku}/files" \
  RCLONE_CONFIG_VAULT_FILENAME_ENCRYPTION=standard RCLONE_CONFIG_VAULT_DIRECTORY_NAME_ENCRYPTION=true
RCLONE_CONFIG_VAULT_PASSWORD=$(rclone obscure "$BACKUP_CIPHER_PASS")
RCLONE_CONFIG_VAULT_PASSWORD2=$(rclone obscure "files-salt:$BACKUP_CIPHER_PASS")
export RCLONE_CONFIG_VAULT_PASSWORD RCLONE_CONFIG_VAULT_PASSWORD2
# Remotes come from the environment only.
export RCLONE_CONFIG=/dev/null
# Small machines: few parallel transfers, bounded buffers.
export RCLONE_TRANSFERS=2 RCLONE_CHECKERS=4 RCLONE_BUFFER_SIZE=4M RCLONE_S3_UPLOAD_CONCURRENCY=2 RCLONE_STATS_LOG_LEVEL=NOTICE

state_get() { [ -f "$STATE_FILE" ] && sed -n "s/^$1=//p" "$STATE_FILE" | tail -1; }
state_set() {
  mkdir -p "$STATUS_DIR"
  { [ -f "$STATE_FILE" ] && grep -v "^$1=" "$STATE_FILE"; echo "$1=$2"; } > "$STATE_FILE.tmp" && mv "$STATE_FILE.tmp" "$STATE_FILE"
}

write_status() {
  mkdir -p "$STATUS_DIR"
  now=$(date -u +%s)
  ok=$(state_get last_success); ok=${ok:-0}
  run=$(state_get last_run); run=${run:-0}
  result=$(state_get last_result); result=${result:-0}
  objects=$(state_get objects); objects=${objects:-0}
  bytes=$(state_get bytes); bytes=${bytes:-0}
  enabled=0; configured && enabled=1
  cat > "$STATUS_DIR/.files.prom.tmp" <<EOF
# HELP konnyaku_files_backup_configured Off-site copy of the files bucket configured (BACKUP_S3_BUCKET set).
# TYPE konnyaku_files_backup_configured gauge
konnyaku_files_backup_configured $enabled
# HELP konnyaku_files_backup_heartbeat_timestamp_seconds The files-backup service is alive.
# TYPE konnyaku_files_backup_heartbeat_timestamp_seconds gauge
konnyaku_files_backup_heartbeat_timestamp_seconds $now
# HELP konnyaku_files_backup_last_success_timestamp_seconds Last successful copy of the files bucket.
# TYPE konnyaku_files_backup_last_success_timestamp_seconds gauge
konnyaku_files_backup_last_success_timestamp_seconds $ok
# HELP konnyaku_files_backup_last_run_timestamp_seconds Last copy attempt.
# TYPE konnyaku_files_backup_last_run_timestamp_seconds gauge
konnyaku_files_backup_last_run_timestamp_seconds $run
# HELP konnyaku_files_backup_last_run_success 1 when the last copy succeeded.
# TYPE konnyaku_files_backup_last_run_success gauge
konnyaku_files_backup_last_run_success $result
# HELP konnyaku_files_backup_objects Objects in the bucket at the last successful copy.
# TYPE konnyaku_files_backup_objects gauge
konnyaku_files_backup_objects $objects
# HELP konnyaku_files_backup_bytes Bytes in the bucket at the last successful copy.
# TYPE konnyaku_files_backup_bytes gauge
konnyaku_files_backup_bytes $bytes
EOF
  mv "$STATUS_DIR/.files.prom.tmp" "$STATUS_DIR/files.prom"
  printf '{"configured":%s,"last_success":%s,"last_run":%s,"last_run_success":%s,"objects":%s,"bytes":%s}\n' \
    "$enabled" "$ok" "$run" "$result" "$objects" "$bytes" > "$STATUS_DIR/.files.json.tmp"
  mv "$STATUS_DIR/.files.json.tmp" "$STATUS_DIR/files.json"
}

run_backup() {
  configured || { log "BACKUP_S3_BUCKET is empty: off-site copy of the files is OFF"; return 1; }
  today=$(date -u +%F)
  state_set last_run "$(date -u +%s)"
  log "files backup: src:$FILES_SOURCE_BUCKET -> vault:current (changed/deleted -> history/$today)"
  if rclone sync "src:$FILES_SOURCE_BUCKET" vault:current --backup-dir "vault:history/$today" --fast-list --stats 0; then
    size=$(rclone size "src:$FILES_SOURCE_BUCKET" --json 2> /dev/null || echo '{}')
    state_set objects "$(echo "$size" | sed -n 's/.*"count":\([0-9]*\).*/\1/p')"
    state_set bytes "$(echo "$size" | sed -n 's/.*"bytes":\([0-9]*\).*/\1/p')"
    state_set last_success "$(date -u +%s)"
    state_set last_result 1
    prune_history
    log "files backup: ok"
    write_status
    return 0
  fi
  state_set last_result 0
  log "files backup: FAILED" >&2
  write_status
  return 1
}

# history/<day> folders older than BACKUP_RETENTION_DAYS are removed.
prune_history() {
  cutoff=$(date -u -d "@$(( $(date -u +%s) - ${BACKUP_RETENTION_DAYS:-30} * 86400 ))" +%F)
  rclone lsf --dirs-only vault:history 2> /dev/null | tr -d / | while read -r day; do
    if [ -n "$day" ] && [ "$day" \< "$cutoff" ]; then
      log "files backup: removing history/$day"
      rclone purge "vault:history/$day" || true
    fi
  done
}

run_restore() {
  configured || { log "BACKUP_S3_BUCKET is empty: nothing to restore from" >&2; return 1; }
  log "restore: vault:current -> src:$FILES_SOURCE_BUCKET (existing newer files are kept)"
  rclone copy vault:current "src:$FILES_SOURCE_BUCKET" --update --fast-list --stats 30s || return 1
  if [ -n "${1:-}" ]; then
    log "restore: vault:history/$1 -> src:$FILES_SOURCE_BUCKET (files removed or replaced that day)"
    rclone copy "vault:history/$1" "src:$FILES_SOURCE_BUCKET" --fast-list --stats 30s || return 1
  fi
  log "restore: done"
}

daemon() {
  trap 'log "stopping"; exit 0' TERM INT
  at="${BACKUP_TIME_UTC:-19:30}"
  log "files-backup service: daily at $at UTC"
  configured || log "BACKUP_S3_BUCKET is empty: off-site copy of the files is OFF. Status files still report it."
  while true; do
    if configured; then
      today=$(date -u +%F)
      now=$(date -u +%s)
      slot=$(date -u -d "$today $at:00" +%s 2> /dev/null || echo 0)
      # Due at the daily slot, or right away when the last success is older than a day (service was down).
      last=$(state_get last_success); last=${last:-0}
      if { [ "$now" -ge "$slot" ] && [ "$(state_get scheduled_on)" != "$today" ]; } || [ $((now - last)) -gt 93600 ]; then
        state_set scheduled_on "$today"
        run_backup || true
      fi
    fi
    write_status
    sleep 60 &
    wait $!
  done
}

command="${1:-daemon}"
[ $# -gt 0 ] && shift
case "$command" in
  daemon) daemon ;;
  backup) run_backup ;;
  restore) run_restore "$@" ;;
  status) write_status; cat "$STATUS_DIR/files.json" ;;
  *) exec rclone "$command" "$@" ;;
esac
