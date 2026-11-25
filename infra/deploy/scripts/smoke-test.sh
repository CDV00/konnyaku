#!/usr/bin/env bash
# Starts freshly built images (api, web, signaling; worker/migrator runtime) and checks they run on this machine's architecture (CI and Deploy workflows).
# Usage: smoke-test.sh <image prefix> <tag>   e.g. smoke-test.sh ghcr.io/cdv00/konnyaku 1a2b3c4d5e6f-arm64
# No database is needed: /health and /api/health do not touch PostgreSQL.
set -euo pipefail
prefix="$1"
tag="$2"

cleanup() { docker rm -f kk-smoke-api kk-smoke-web kk-smoke-signaling > /dev/null 2>&1 || true; }
trap cleanup EXIT

wait_for() {
  for _ in $(seq 1 45); do
    if curl -fsS "$1" > /dev/null 2>&1; then echo "ok $1"; return 0; fi
    sleep 2
  done
  echo "timeout $1" >&2
  return 1
}

# DbContexts are resolved per request even for /health, so every module needs a connection string; nothing connects.
db_env=()
for module in platform catalog identity organization academic crm scheduling billing workspace learning hr; do
  db_env+=(-e "ConnectionStrings__${module}=Host=127.0.0.1;Database=none;Username=none;Password=none")
done

docker run -d --name kk-smoke-api -p 18080:8080 "${db_env[@]}" \
  -e Auth__SigningKey=smoke-test-signing-key-0123456789abcdef \
  -e Platform__AdminKey=smoke -e Platform__BaseDomain=localhost \
  "$prefix-api:$tag" > /dev/null
docker run -d --name kk-smoke-web -p 13000:3000 "$prefix-web:$tag" > /dev/null
docker run -d --name kk-smoke-signaling -p 15100:5100 -e SIGNALING_SECRET=smoke-test-signaling-secret-0123456789 \
  "$prefix-signaling:$tag" > /dev/null

if ! wait_for http://localhost:18080/health || ! wait_for http://localhost:13000/api/health \
  || ! wait_for http://localhost:15100/rtc/health; then
  docker logs kk-smoke-api || true
  docker logs kk-smoke-web || true
  docker logs kk-smoke-signaling || true
  exit 1
fi
# The image's own health probe (distroless: no shell, the binary checks itself).
docker exec kk-smoke-signaling /usr/local/bin/konnyaku-signaling healthcheck
echo "ok signaling healthcheck"

# Worker and migrator need PostgreSQL to do anything; check the runtime starts and the app loads.
for image in worker migrator; do
  docker run --rm --entrypoint dotnet "$prefix-$image:$tag" --list-runtimes | grep -q Microsoft.NETCore.App
  echo "ok $image runtime"
done
echo "smoke test passed on $(uname -m)"
