#!/bin/sh
# Prometheus and Alertmanager read no environment variables: their configs are templates whose ${NAME} placeholders
# are filled from the container environment at start (busybox awk; no envsubst in these images).
# Usage: render.sh <template> <output>
set -eu
awk '{
  while (match($0, /\$\{[A-Za-z0-9_]+\}/)) {
    name = substr($0, RSTART + 2, RLENGTH - 3)
    $0 = substr($0, 1, RSTART - 1) ENVIRON[name] substr($0, RSTART + RLENGTH)
  }
  print
}' "$1" > "$2"
