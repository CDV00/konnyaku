#!/usr/bin/env bash
# Prints fresh secrets as .env lines: ./scripts/gen-secrets.sh >> .env
# Run once per environment. Changing DB passwords later needs ALTER ROLE as well (the init script only runs on an empty volume).
set -euo pipefail

secret() { openssl rand -base64 48 | tr -d '\n/+=' | cut -c1-"${1:-40}"; }

echo "# --- generated $(date -u +%Y-%m-%dT%H:%M:%SZ) ---"
echo "POSTGRES_PASSWORD=$(secret)"
for module in PLATFORM CATALOG IDENTITY ORGANIZATION ACADEMIC CRM SCHEDULING BILLING WORKSPACE LEARNING HR; do
  echo "DB_PASSWORD_${module}=$(secret)"
done
# JWT signing key shared by every API replica (>= 32 bytes). Rotating it signs everybody out.
echo "AUTH_SIGNING_KEY=$(secret 64)"
# X-Platform-Key for platform operations (creating tenants); the API is not reachable from the internet.
echo "PLATFORM_ADMIN_KEY=$(secret)"
