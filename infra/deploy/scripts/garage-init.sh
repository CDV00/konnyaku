#!/usr/bin/env bash
# Prepares Garage (service `garage`) for the Content module; safe to run again (scripts/deploy.sh runs it on every deploy):
#   1. gives the single node its storage role (cluster layout), once;
#   2. creates the bucket S3_BUCKET;
#   3. imports the application key S3_ACCESS_KEY / S3_SECRET_KEY (from gen-secrets.sh) with read/write on that bucket,
#      and owner (the API sets the bucket CORS rules with it).
# CORS rules of the bucket are applied by the API at start-up (Storage__S3__CorsOrigins).
# Not needed when S3_ENDPOINT points to a provider's object storage (GARAGE_REPLICAS=0). docs/trien-khai.md#kho-file-garage
set -euo pipefail
cd "$(dirname "$0")/.."

if [[ -f .env ]]; then
  set -a
  # shellcheck disable=SC1091
  source .env
  set +a
fi

if [[ "${GARAGE_REPLICAS:-1}" == 0 ]]; then
  echo "garage-init: GARAGE_REPLICAS=0 (external object storage), nothing to do"
  exit 0
fi

bucket="${S3_BUCKET:-konnyaku}"
key_id="${S3_ACCESS_KEY:?S3_ACCESS_KEY missing (scripts/gen-secrets.sh)}"
key_secret="${S3_SECRET_KEY:?S3_SECRET_KEY missing (scripts/gen-secrets.sh)}"
capacity="${GARAGE_CAPACITY:-100G}"
# Override to run against another container (e.g. the dev stack): GARAGE="docker exec -i my-garage /garage".
read -r -a garage <<< "${GARAGE:-docker compose exec -T -e RUST_LOG=warn garage /garage}"

for _ in $(seq 1 30); do
  "${garage[@]}" status > /dev/null 2>&1 && break
  sleep 2
done

node=$("${garage[@]}" node id -q 2> /dev/null | cut -d@ -f1)
[[ -n "$node" ]] || { echo "garage-init: cannot reach Garage" >&2; exit 1; }

# A fresh node has no role: the layout has version 0 and the node is not in it.
if "${garage[@]}" layout show 2> /dev/null | grep -q "No nodes currently have a role"; then
  "${garage[@]}" layout assign -z dc1 -c "$capacity" "${node:0:16}" > /dev/null
  version=$("${garage[@]}" layout show | sed -n 's/.*--version \([0-9]\+\).*/\1/p' | head -1)
  "${garage[@]}" layout apply --version "${version:-1}" > /dev/null
  echo "garage-init: layout applied (zone dc1, capacity $capacity)"
fi

if ! "${garage[@]}" bucket info "$bucket" > /dev/null 2>&1; then
  "${garage[@]}" bucket create "$bucket" > /dev/null
  echo "garage-init: bucket $bucket created"
fi

if ! "${garage[@]}" key info "$key_id" > /dev/null 2>&1; then
  "${garage[@]}" key import --yes -n konnyaku-app "$key_id" "$key_secret" > /dev/null
  echo "garage-init: key konnyaku-app imported"
fi
"${garage[@]}" bucket allow --read --write --owner "$bucket" --key "$key_id" > /dev/null
echo "garage-init: bucket $bucket ready"
