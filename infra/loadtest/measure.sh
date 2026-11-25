#!/usr/bin/env bash
# Measures memory and CPU of every container of a running compose project: idle, under k6 load, and after the load.
# Usage: measure.sh <compose project> <label> [load|stress] [learner temporary password]
# Writes <label>.stats.csv (every sample), <label>.summary.txt (per container: idle, peak, after; avg CPU under load)
# and <label>.k6.txt (k6 summary with p95 per page) into $OUT (default: current directory).
set -euo pipefail
project="$1"
label="$2"
profile="${3:-load}"
learner_temp="${4:-}"
out="${OUT:-.}"
here="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$out"
csv="$out/$label.stats.csv"
echo "phase,time,container,mem_mib,cpu_pct" > "$csv"

# docker stats prints "123.4MiB / 7.6GiB"; convert the first value to MiB.
sample() {
  local phase="$1"
  docker stats --no-stream --format '{{.Name}},{{.MemUsage}},{{.CPUPerc}}' \
    $(docker ps -q --filter "label=com.docker.compose.project=$project") |
    awk -F, -v phase="$phase" -v t="$(date +%s)" '{
      split($2, m, " / "); v = m[1]; n = v + 0;
      if (v ~ /GiB/) n *= 1024; else if (v ~ /KiB/) n /= 1024; else if (v ~ /[0-9]B$/ && v !~ /MiB/) n /= 1048576;
      gsub("%", "", $3); printf "%s,%s,%s,%.1f,%s\n", phase, t, $1, n, $3 }' >> "$csv"
}

echo "idle samples..."
for _ in 1 2 3; do sample idle; sleep 2; done

echo "k6 $profile..."
network="${project}_edge"
MSYS_NO_PATHCONV=1 docker run --rm --name "k6-$label" --network "$network" -v "$here:/s:ro" grafana/k6:0.57.0 run \
  --quiet -e BASE=http://caddy -e PROFILE="$profile" -e LEARNER_TEMP="$learner_temp" -e DURATION="${DURATION:-2m}" \
  /s/mixed.js > "$out/$label.k6.txt" 2>&1 &
k6_pid=$!
sleep 5
while kill -0 "$k6_pid" 2> /dev/null; do sample load; sleep 1; done
wait "$k6_pid" || echo "k6 exited non-zero (thresholds?), see $out/$label.k6.txt"

echo "after-load samples (30 s later)..."
sleep 30
for _ in 1 2 3; do sample after; sleep 2; done

awk -F, 'NR > 1 {
    key = $3; seen[key] = 1
    if ($1 == "idle") { idle[key] += $4; ni[key]++ }
    if ($1 == "load") { if ($4 > peak[key]) peak[key] = $4; cpu[key] += $5; nl[key]++; if ($5 > cmax[key]) cmax[key] = $5 }
    if ($1 == "after") { after[key] += $4; na[key]++ }
  }
  END {
    printf "%-34s %9s %9s %9s %9s %9s\n", "container", "idle MiB", "peak MiB", "after MiB", "avg CPU%", "max CPU%"
    for (k in seen) {
      i = ni[k] ? idle[k] / ni[k] : 0; a = na[k] ? after[k] / na[k] : 0; c = nl[k] ? cpu[k] / nl[k] : 0
      printf "%-34s %9.0f %9.0f %9.0f %9.0f %9.0f\n", k, i, peak[k], a, c, cmax[k]
      ti += i; tp += peak[k]; ta += a; tc += c
    }
    printf "%-34s %9.0f %9.0f %9.0f %9.0f\n", "TOTAL (peak = sum of peaks)", ti, tp, ta, tc
  }' "$csv" | sort > "$out/$label.summary.txt"
cat "$out/$label.summary.txt"
grep -E "http_req_duration\{page|http_req_failed|http_reqs|checks" "$out/$label.k6.txt" || true
