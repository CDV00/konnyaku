#!/usr/bin/env bash
# Prints fresh secrets as .env lines: ./scripts/gen-secrets.sh >> .env
# Run once per environment. Changing DB passwords later needs ALTER ROLE as well (the init script only runs on an empty volume).
set -euo pipefail

secret() { openssl rand -base64 48 | tr -d '\n/+=' | cut -c1-"${1:-40}"; }

echo "# --- generated $(date -u +%Y-%m-%dT%H:%M:%SZ) ---"
echo "POSTGRES_PASSWORD=$(secret)"
for module in PLATFORM CATALOG IDENTITY ORGANIZATION ACADEMIC CRM SCHEDULING BILLING WORKSPACE LEARNING HR NOTIFICATION ASSESSMENT; do
  echo "DB_PASSWORD_${module}=$(secret)"
done
# JWT signing key shared by every API replica (>= 32 bytes). Rotating it signs everybody out.
echo "AUTH_SIGNING_KEY=$(secret 64)"
# X-Platform-Key for platform operations (creating tenants); the API is not reachable from the internet.
echo "PLATFORM_ADMIN_KEY=$(secret)"
# HMAC key signing the proof-of-work captcha of public forms (>= 32 bytes), shared by every API replica.
echo "CAPTCHA_KEY=$(secret 64)"
# Built-in classroom: tickets between API and signaling (>= 32 bytes), and coturn's TURN REST shared secret.
echo "SIGNALING_SECRET=$(secret 48)"
echo "TURN_SECRET=$(secret 48)"
# Redis (shared rate-limit counters of the API replicas). Letters and digits only: it goes into a connection string.
echo "REDIS_PASSWORD=$(secret 40)"
# Monitoring profile: Grafana "admin" password and the read-only statistics role of postgres-exporter.
echo "GRAFANA_ADMIN_PASSWORD=$(secret 24)"
echo "DB_PASSWORD_MONITOR=$(secret)"
