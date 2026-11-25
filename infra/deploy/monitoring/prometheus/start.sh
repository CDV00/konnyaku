#!/bin/sh
# Entry point of the prometheus service: render config + alert rules from .env values, then run Prometheus.
set -eu
out=/prometheus/config
mkdir -p "$out"
sh /etc/konnyaku/render.sh /etc/konnyaku/prometheus/prometheus.yml.tmpl "$out/prometheus.yml"
sh /etc/konnyaku/render.sh /etc/konnyaku/prometheus/rules.yml.tmpl "$out/rules.yml"
exec /bin/prometheus \
  --config.file="$out/prometheus.yml" \
  --storage.tsdb.path=/prometheus \
  --storage.tsdb.retention.time="${PROMETHEUS_RETENTION:-15d}" \
  --storage.tsdb.retention.size="${PROMETHEUS_RETENTION_SIZE:-4GB}" \
  --web.enable-otlp-receiver \
  --web.enable-remote-write-receiver
