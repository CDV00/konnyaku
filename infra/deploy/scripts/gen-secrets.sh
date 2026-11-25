#!/usr/bin/env bash
# Prints fresh secrets as .env lines: ./scripts/gen-secrets.sh >> .env
# Run once per environment. Changing DB passwords later needs ALTER ROLE as well (the init script only runs on an empty volume).
set -euo pipefail

secret() { openssl rand -base64 48 | tr -d '\n/+=' | cut -c1-"${1:-40}"; }

echo "# --- generated $(date -u +%Y-%m-%dT%H:%M:%SZ) ---"
echo "POSTGRES_PASSWORD=$(secret)"
for module in PLATFORM CATALOG IDENTITY ORGANIZATION ACADEMIC CRM SCHEDULING BILLING WORKSPACE LEARNING HR NOTIFICATION ASSESSMENT CONTENT; do
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
# Encryption key of the pgBackRest repository (client-side, AES-256). Store it OUTSIDE this server as well (team
# password manager): the backups cannot be read without it, and a lost server takes this .env with it.
echo "BACKUP_CIPHER_PASS=$(secret 64)"
# Redis (rate-limit counters, realtime signals). Letters and digits only: it goes into a connection string and a URL.
echo "REDIS_PASSWORD=$(secret 40)"
# Web Push (VAPID, RFC 8292): a P-256 key pair, base64url. The public key is handed to browsers when they subscribe;
# changing the pair later makes every browser subscribe again (old subscriptions are refused by the push services).
vapid=$(mktemp)
openssl ecparam -name prime256v1 -genkey -noout -out "$vapid" 2> /dev/null
b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }
echo "VAPID_PUBLIC_KEY=$(openssl ec -in "$vapid" -pubout -outform DER 2> /dev/null | tail -c 65 | b64url)"
echo "VAPID_PRIVATE_KEY=$(openssl ec -in "$vapid" -outform DER 2> /dev/null | head -c 39 | tail -c 32 | b64url)"
rm -f "$vapid"
# Monitoring profile: Grafana "admin" password and the read-only statistics role of postgres-exporter.
echo "GRAFANA_ADMIN_PASSWORD=$(secret 24)"
echo "DB_PASSWORD_MONITOR=$(secret)"
# Garage (object storage of the files): cluster RPC secret (32 bytes hex), admin API token, and the S3 key of the
# application (Garage format: GK + 24 hex characters, secret 64 hex), imported by scripts/garage-init.sh.
echo "GARAGE_RPC_SECRET=$(openssl rand -hex 32)"
echo "GARAGE_ADMIN_TOKEN=$(secret 48)"
echo "S3_ACCESS_KEY=GK$(openssl rand -hex 12)"
echo "S3_SECRET_KEY=$(openssl rand -hex 32)"
