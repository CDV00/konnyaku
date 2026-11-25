#!/usr/bin/env bash
# Restores the production database from the pgBackRest repository (object storage), on this server or on a NEW one.
#   ./scripts/restore.sh --list                                    backups and WAL range (what can be restored)
#   ./scripts/restore.sh latest                                    newest state in the repository (server lost)
#   ./scripts/restore.sh --time "2026-11-25 10:30:00+07"           state at that moment (data deleted by mistake)
#   ./scripts/restore.sh --name before-restore-20261125T040000Z    a restore point (e.g. the one this script creates)
# Options: --yes (no question), --timeline <n> (restore a moment of an older timeline, i.e. undo a previous restore),
#          --set <backup label> (start from this backup instead of the newest suitable one).
# It stops the site, replaces the database, waits until PostgreSQL has replayed the WAL, starts everything again and
# takes a new full backup. Everything written after the chosen moment is lost, for every center: to recover only some
# rows, use `./scripts/kk-backup.sh drill --time "<ts>" --keep` and copy them out instead. Runbook: docs/trien-khai.md
set -euo pipefail
cd "$(dirname "$0")/.."

mode="" target="" yes=0 extra=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --list) mode=list; shift ;;
    latest) mode=latest; shift ;;
    --time) mode=time; target="${2:?time}"; shift 2 ;;
    --name) mode=name; target="${2:?restore point}"; shift 2 ;;
    --timeline) extra+=("--target-timeline=${2:?timeline}"); shift 2 ;;
    --set) extra+=("--set=${2:?label}"); shift 2 ;;
    --yes) yes=1; shift ;;
    *) echo "unknown argument $1 (see the head of this script)" >&2; exit 2 ;;
  esac
done
[[ -n "$mode" ]] || { sed -n '2,12p' "$0"; exit 2; }

set -a
# shellcheck disable=SC1091
source .env
set +a
if [[ -z "${BACKUP_S3_BUCKET:-}" ]]; then
  echo "BACKUP_S3_BUCKET is empty in .env: there is no repository to restore from." >&2
  exit 1
fi

log() { echo "$(date -u +%FT%TZ) $*"; }
kk() { docker compose run --rm --no-deps -T backup "$@"; }

# New server: the image is built from postgres/Dockerfile.
docker image inspect konnyaku-postgres:17 > /dev/null 2>&1 || docker compose build postgres

kk info
[[ "$mode" == list ]] && exit 0
# Nothing is stopped unless the repository is readable and holds a backup.
if ! kk info --output=json | grep -q '"status":{"code":0'; then
  echo "The repository is not readable or holds no backup (see above): nothing was changed." >&2
  exit 1
fi

case "$mode" in
  latest) what="the newest state in the repository" ;;
  time) what="its state at $target" ;;
  name) what="restore point $target" ;;
esac
if [[ $yes != 1 ]]; then
  echo
  echo "This stops the site and REPLACES the database with $what."
  echo "Everything written after that moment is lost (it stays in the repository on the old timeline)."
  read -r -p "Type RESTORE to continue: " answer
  [[ "$answer" == RESTORE ]] || { echo "cancelled"; exit 1; }
fi

started=$(date -u +%s)
if [[ -n "$(docker compose ps --status running -q postgres 2> /dev/null)" ]]; then
  # The current state stays restorable: mark it and push the newest WAL before stopping.
  point="before-restore-$(date -u +%Y%m%dT%H%M%SZ)"
  if docker compose exec -T postgres psql -XAtq -U konnyaku -d konnyaku -c "select pg_create_restore_point('$point')" > /dev/null \
    && docker compose exec -T postgres psql -XAtq -U konnyaku -d konnyaku -c "select pg_switch_wal()" > /dev/null; then
    log "current state marked as restore point $point (timeline $(docker compose exec -T postgres psql -XAtq -U konnyaku -d konnyaku \
      -c "select timeline_id from pg_control_checkpoint()"))"
  fi
fi

log "stopping the site and PostgreSQL"
docker compose stop web api worker signaling backup
docker compose stop postgres

case "$mode" in
  latest) kk restore latest "${extra[@]+"${extra[@]}"}" ;;
  *) kk restore "$mode" "$target" "${extra[@]+"${extra[@]}"}" ;;
esac

log "starting PostgreSQL: replaying WAL up to the target"
docker compose up -d --no-deps postgres
for i in $(seq 1 900); do
  state=$(docker compose exec -T postgres psql -XAtq -U konnyaku -d konnyaku -c "select pg_is_in_recovery()" 2> /dev/null || true)
  [[ "$state" == f ]] && break
  if [[ $i == 900 ]]; then
    echo "PostgreSQL did not finish recovery in 30 min: docker compose logs postgres" >&2
    exit 1
  fi
  sleep 2
done
log "PostgreSQL is open: $(docker compose exec -T postgres psql -XAtq -U konnyaku -d konnyaku \
  -c "select 'timeline ' || timeline_id || ', last replayed transaction ' || coalesce(pg_last_xact_replay_timestamp()::text, 'n/a') from pg_control_checkpoint()")"

docker compose up -d
log "site is back after $(( $(date -u +%s) - started )) s; taking a full backup of the new timeline"
docker compose exec -T backup /opt/konnyaku/bin/kk-backup backup full \
  || log "warning: the full backup failed; run ./scripts/kk-backup.sh backup full (the daily one also makes a full)"
log "done in $(( $(date -u +%s) - started )) s"
