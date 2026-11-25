#!/usr/bin/env bash
# Rolling deploy without downtime: ./scripts/deploy.sh <image tag>      (rollback: the same with the previous tag)
#   --signaling auto|now|skip   classroom signaling (rooms in memory): auto (default) replaces it only when its image
#                               changed AND no room is open; now replaces it anyway (open rooms reconnect in seconds)
#   --no-pull                   use images already on this server
#
# 1. Pulls the images of <tag> (IMAGE_TAG in .env is set to it), builds the PostgreSQL image if missing.
# 2. Infrastructure (postgres, backup, redis, caddy, coturn, monitoring) is only recreated when its definition changed;
#    the Caddyfile is reloaded gracefully.
# 3. Migrator runs first; if it fails nothing else changes. Migrations must work with the previous version too
#    (docs/trien-khai.md#migration-tương-thích-ngược): during the roll both versions serve requests.
# 4. api, web, worker: ONE replica at a time: start a new one, wait until it is healthy, then stop an old one
#    (SIGTERM: it stops accepting and finishes what it is doing). A new replica that does not become healthy is removed
#    and the deploy stops with the old version still serving.
# Runbook: docs/trien-khai.md#deploy
set -euo pipefail
cd "$(dirname "$0")/.."

tag="" signaling=auto pull=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --signaling) signaling="${2:?auto|now|skip}"; shift 2 ;;
    --no-pull) pull=0; shift ;;
    -*) echo "unknown option $1" >&2; exit 2 ;;
    *) tag="$1"; shift ;;
  esac
done

log() { echo "$(date -u +%FT%TZ) $*"; }
fail() { log "DEPLOY FAILED: $*" >&2; exit 1; }

if [[ -n "$tag" ]]; then
  if grep -q '^IMAGE_TAG=' .env; then sed -i "s/^IMAGE_TAG=.*/IMAGE_TAG=$tag/" .env; else echo "IMAGE_TAG=$tag" >> .env; fi
fi
set -a
# shellcheck disable=SC1091
source .env
set +a
tag="${IMAGE_TAG:-latest}"
started=$(date -u +%s)
log "deploying $tag"

# Seconds to wait after a new API replica is healthy before an old one stops (web resolves Docker DNS per connection).
settle="${DEPLOY_SETTLE_SECONDS:-6}"
health_timeout="${DEPLOY_HEALTH_TIMEOUT:-180}"

if [[ $pull == 1 ]]; then
  docker compose pull --quiet api web worker migrator signaling
fi
docker image inspect konnyaku-postgres:17 > /dev/null 2>&1 || docker compose build postgres

# Infrastructure: created when missing, recreated only when its definition changed (postgres: a restart).
# The backup service runs its script in a loop: a changed script (postgres/backup) changes its definition.
BACKUP_SCRIPTS_VERSION=$(cat postgres/backup/* | sha256sum | cut -c1-12)
export BACKUP_SCRIPTS_VERSION
docker compose up -d --no-deps --wait postgres
# Everything else that is not rolled below (backup, redis, caddy, coturn, the monitoring profile when enabled).
mapfile -t infra < <(docker compose config --services | grep -vxE 'postgres|migrator|api|web|worker|signaling')
docker compose up -d --no-deps "${infra[@]}"

# Caddy finds the web replicas through Docker DNS (caddy/web-upstreams.caddy), which lists a container until it is
# gone, and caches that list for 5 s: a POST sent to a replica that just disappeared cannot be retried. While web
# rolls, Caddy gets a static list instead, without the replica about to be stopped (graceful reload: requests in
# flight finish), and the DNS lookup comes back at the end, whatever happens.
upstreams_file=caddy/web-upstreams.caddy
caddy_reload() {
  docker compose exec -T caddy caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile > /dev/null 2>&1 \
    || log "warning: caddy reload failed (docker compose logs caddy)"
}
web_upstreams_static() { # container ids that keep receiving requests
  local names=() id
  for id in "$@"; do names+=("$(docker inspect -f '{{.Name}}' "$id" | tr -d /):3000"); done
  printf '# Written by scripts/deploy.sh during a rolling deploy, restored at its end.\nto %s\n' "${names[*]}" > "$upstreams_file"
  caddy_reload
}
web_upstreams_dynamic() {
  grep -q '^dynamic a' "$upstreams_file" 2> /dev/null && return 0
  printf '%s\n' '# Upstreams of the web replicas, imported by the Caddyfile. At rest: every container Docker DNS returns for "web".' \
    '# scripts/deploy.sh replaces this file with a static list during a rolling deploy (so Caddy stops sending requests to' \
    '# a replica before it is stopped) and puts this content back at the end.' \
    'dynamic a {' '	name web' '	port 3000' '	refresh 5s' '}' > "$upstreams_file"
  caddy_reload
}
trap web_upstreams_dynamic EXIT
web_upstreams_dynamic
caddy_reload

migrator_log=$(mktemp)
if ! docker compose run --rm --no-deps -T migrator > "$migrator_log" 2>&1; then
  tail -40 "$migrator_log" >&2
  fail "migrator failed; nothing else was changed"
fi
log "migrations: $(grep -c 'Applying migration' "$migrator_log" || true) applied"
grep 'Applying migration' "$migrator_log" | sed 's/^ */  /' || true
rm -f "$migrator_log"

image_of() { # service -> configured image reference
  case "$1" in
    api) echo "${IMAGE_API:-ghcr.io/OWNER/konnyaku-api}:$tag" ;;
    web) echo "${IMAGE_WEB:-ghcr.io/OWNER/konnyaku-web}:$tag" ;;
    worker) echo "${IMAGE_WORKER:-ghcr.io/OWNER/konnyaku-worker}:$tag" ;;
    signaling) echo "${IMAGE_SIGNALING:-ghcr.io/OWNER/konnyaku-signaling}:$tag" ;;
  esac
}

containers() { docker compose ps -a -q "$1" 2> /dev/null; }

wait_ready() { # container id -> 0 when healthy (or running for 10 s without a healthcheck)
  local id="$1" deadline=$(( $(date -u +%s) + health_timeout )) state health
  while (( $(date -u +%s) < deadline )); do
    state=$(docker inspect -f '{{.State.Status}}' "$id")
    health=$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$id")
    case "$state/$health" in
      running/healthy) return 0 ;;
      running/none) sleep 10; [[ "$(docker inspect -f '{{.State.Status}}' "$id")" == running ]] && return 0 ;;
      exited/* | dead/*) return 1 ;;
    esac
    sleep 2
  done
  return 1
}

# roll <service> <replicas wanted>
roll() {
  local service="$1" want="$2" old new id current serving=()
  mapfile -t old < <(containers "$service")
  if (( want == 0 )); then
    for id in "${old[@]}"; do docker stop "$id" > /dev/null && docker rm "$id" > /dev/null; done
    log "$service: 0 replicas"
    return 0
  fi
  log "$service: ${#old[@]} old -> $want new ($(image_of "$service"))"
  # Web: Caddy only sends requests to the replicas in `serving` (a new container is in Docker DNS before it listens).
  serving=("${old[@]}")
  if [[ "$service" == web && ${#serving[@]} -gt 0 ]]; then web_upstreams_static "${serving[@]}"; fi
  local created=0
  local seen=("${old[@]}")
  while (( created < want )); do
    current=$(containers "$service" | wc -l)
    docker compose up -d --no-deps --no-recreate --scale "$service=$((current + 1))" "$service" > /dev/null 2>&1 \
      || fail "$service: could not start a new replica"
    new=$(containers "$service" | grep -vxF -f <(printf '%s\n' "${seen[@]}" "") | head -1)
    [[ -n "$new" ]] || fail "$service: new replica not found"
    seen+=("$new")
    if ! wait_ready "$new"; then
      docker logs --tail 50 "$new" >&2 || true
      docker rm -f "$new" > /dev/null
      fail "$service: new replica did not become healthy in ${health_timeout}s; the previous version keeps serving"
    fi
    created=$((created + 1))
    serving+=("$new")
    log "$service: new replica $(docker inspect -f '{{.Name}}' "$new") healthy"
    if (( ${#old[@]} > 0 )); then
      id="${old[0]}"; old=("${old[@]:1}")
      mapfile -t serving < <(printf '%s\n' "${serving[@]}" | grep -vxF "$id")
      if [[ "$service" == web ]]; then
        web_upstreams_static "${serving[@]}"
        sleep 2
      else
        # API callers (web) resolve Docker DNS on every new connection: give the new replica time to take traffic.
        sleep "$settle"
      fi
      log "$service: stopping old replica $(docker inspect -f '{{.Name}}' "$id")"
      docker stop "$id" > /dev/null && docker rm "$id" > /dev/null
    elif [[ "$service" == web ]]; then
      web_upstreams_static "${serving[@]}"
    fi
  done
  # More old replicas than wanted (replicas lowered in .env): already out of `serving`.
  if [[ "$service" == web && ${#old[@]} -gt 0 ]]; then web_upstreams_static "${serving[@]:${#old[@]}}"; sleep 2; fi
  for id in "${old[@]}"; do docker stop "$id" > /dev/null && docker rm "$id" > /dev/null; done
}

roll api "${API_REPLICAS:-2}"
roll web "${WEB_REPLICAS:-2}"
web_upstreams_dynamic
roll worker "${WORKER_REPLICAS:-1}"

# Signaling keeps the open classrooms in memory: replacing it drops them for a few seconds (browsers reconnect).
signaling_step() {
  if [[ "$signaling" == skip ]]; then
    log "signaling: skipped (--signaling skip); the running version stays"
    return 0
  fi
  local current wanted_id current_id rooms=0
  current=$(containers signaling | head -1)
  wanted_id=$(docker image inspect -f '{{.Id}}' "$(image_of signaling)" 2> /dev/null) \
    || fail "image $(image_of signaling) is not on this server (pull it, or ./scripts/deploy.sh --signaling skip)"
  current_id=none
  [[ -n "$current" ]] && current_id=$(docker inspect -f '{{.Image}}' "$current")
  if [[ "$current_id" == "$wanted_id" ]]; then
    log "signaling: image unchanged, kept"
    return 0
  fi
  if [[ -n "$current" && "$signaling" == auto ]]; then
    rooms=$(docker compose exec -T caddy wget -qO- http://signaling:5100/rtc/health 2> /dev/null | sed -n 's/.*"rooms":\([0-9]*\).*/\1/p')
    rooms="${rooms:-0}"
  fi
  if (( rooms > 0 )); then
    log "signaling: NOT replaced, $rooms classroom(s) open. Later, outside class hours: ./scripts/deploy.sh --no-pull --signaling now"
  else
    docker compose up -d --no-deps signaling > /dev/null
    log "signaling: replaced"
  fi
}
signaling_step

docker image prune -f > /dev/null
log "deployed $tag in $(( $(date -u +%s) - started )) s"
docker compose ps --format 'table {{.Name}}\t{{.Image}}\t{{.Status}}'
