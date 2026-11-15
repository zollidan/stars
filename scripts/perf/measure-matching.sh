#!/usr/bin/env bash
# Measure CPU/RAM used only while the matching worker is draining jobs.
#
# Usage:
#   STARS_API_URL=http://127.0.0.1:5080 \
#   API_CONTAINER=stars-api PG_CONTAINER=stars-postgres \
#   ./scripts/perf/measure-matching.sh
#
# Run it after triggering a sync/import, while /api/metrics shows pendingJobs
# or processingJobs > 0. The script waits up to WAIT_SECONDS for matching to start.
set -euo pipefail

API_URL="${STARS_API_URL:-http://127.0.0.1:5080}"
API_CONTAINER="${API_CONTAINER:-stars-api}"
PG_CONTAINER="${PG_CONTAINER:-stars-postgres}"
ENGINE="${CONTAINER_ENGINE:-podman}"
WAIT_SECONDS="${WAIT_SECONDS:-300}"
INTERVAL="${INTERVAL:-1}"

command -v jq >/dev/null || { echo "jq is required" >&2; exit 1; }
$ENGINE inspect "$API_CONTAINER" >/dev/null 2>&1 || { echo "missing container $API_CONTAINER" >&2; exit 1; }
$ENGINE inspect "$PG_CONTAINER" >/dev/null 2>&1 || { echo "missing container $PG_CONTAINER" >&2; exit 1; }

BEFORE=$(mktemp)
AFTER=$(mktemp)
trap 'rm -f "$BEFORE" "$AFTER"' EXIT

active_jobs() {
  curl -fsS "$API_URL/api/metrics" 2>/dev/null | jq -r '.pendingJobs + .processingJobs' 2>/dev/null || echo 0
}

echo "Waiting for matching jobs (up to ${WAIT_SECONDS}s) ..."
for _ in $(seq 1 "$WAIT_SECONDS"); do
  active=$(active_jobs)
  if [ "$active" -gt 0 ]; then break; fi
  sleep 1
done

if [ "${active:-0}" -eq 0 ]; then
  echo "No active matching jobs. Start a sync/import first, then rerun."
  exit 0
fi

$ENGINE stats --no-stream --format json "$API_CONTAINER" "$PG_CONTAINER" > "$BEFORE"
start=$(date +%s)
echo "Sampling while active=$active ..."
while [ "$active" -gt 0 ]; do
  sleep "$INTERVAL"
  active=$(active_jobs)
done
end=$(date +%s)
$ENGINE stats --no-stream --format json "$API_CONTAINER" "$PG_CONTAINER" > "$AFTER"

python3 - "$BEFORE" "$AFTER" "$API_CONTAINER" "$PG_CONTAINER" "$((end - start))" <<'PY'
import json, re, sys
before_path, after_path, api_name, pg_name, elapsed = sys.argv[1:]
elapsed = max(1, int(elapsed))
def load(path):
    with open(path) as f:
        return json.load(f)
def seconds(value):
    m = re.match(r'(?:(\d+)m)?([0-9.]+)(ms|s)', value)
    if not m:
        raise ValueError(value)
    number = float(m.group(2))
    return int(m.group(1) or 0) * 60 + (number / 1000 if m.group(3) == 'ms' else number)
def mb(value):
    m = re.match(r'([0-9.]+)([KMG]B)', value)
    n = float(m.group(1))
    return n * 1024 if m.group(2) == 'GB' else n
before = {item['name']: item for item in load(before_path)}
after = {item['name']: item for item in load(after_path)}
print(f"window={elapsed}s")
for name in (api_name, pg_name):
    b, a = before[name], after[name]
    cores = (seconds(a['cpu_time']) - seconds(b['cpu_time'])) / elapsed
    print(f"{name:24} cpu_avg={cores:.2f} cores  mem={mb(a['mem_usage']):.0f} MB")
PY
