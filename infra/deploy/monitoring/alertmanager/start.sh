#!/bin/sh
# Entry point of the alertmanager service: writes alertmanager.yml from .env (email via the same SMTP as the app,
# Telegram only when TELEGRAM_BOT_TOKEN and TELEGRAM_CHAT_ID are set), then runs Alertmanager.
set -eu
dir=/alertmanager/config
mkdir -p "$dir"
umask 077

# SMTP_SECURITY of the app: None = plain SMTP (Mailpit in tests), anything else = TLS required (STARTTLS, or implicit
# TLS on port 465).
if [ "${SMTP_SECURITY:-Auto}" = "None" ]; then export ALERT_SMTP_REQUIRE_TLS=false; else export ALERT_SMTP_REQUIRE_TLS=true; fi
printf '%s' "${SMTP_PASSWORD:-}" > "$dir/smtp_password"
export ALERT_EMAIL_FROM="${ALERT_EMAIL_FROM:-${SMTP_FROM_ADDRESS:-}}"
sh /etc/konnyaku/render.sh /etc/konnyaku/alertmanager/alertmanager.yml.tmpl "$dir/alertmanager.yml"

if [ -n "${SMTP_USERNAME:-}" ]; then
  cat >> "$dir/alertmanager.yml" <<EOF
    auth_username: '${SMTP_USERNAME}'
    auth_password_file: $dir/smtp_password
EOF
fi

if [ -n "${TELEGRAM_BOT_TOKEN:-}" ] && [ -n "${TELEGRAM_CHAT_ID:-}" ]; then
  printf '%s' "$TELEGRAM_BOT_TOKEN" > "$dir/telegram_token"
  cat >> "$dir/alertmanager.yml" <<EOF
  telegram_configs:
  - bot_token_file: $dir/telegram_token
    chat_id: ${TELEGRAM_CHAT_ID}
    send_resolved: true
EOF
fi

exec /bin/alertmanager \
  --config.file="$dir/alertmanager.yml" \
  --storage.path=/alertmanager \
  --cluster.listen-address=
