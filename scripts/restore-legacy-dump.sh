#!/usr/bin/env bash
set -euo pipefail

DUMP="${1:-eva-team-directory_backup_2026-11-15_00-00-08.dump}"
CONTAINER="${STARS_LEGACY_CONTAINER:-stars-legacy-pg}"
PORT="${STARS_LEGACY_PORT:-55433}"
ENGINE="${CONTAINER_ENGINE:-}"

if [[ -z "$ENGINE" ]]; then
  if command -v podman >/dev/null 2>&1; then
    ENGINE=podman
  elif command -v docker >/dev/null 2>&1; then
    ENGINE=docker
  else
    echo "Neither podman nor docker is available." >&2
    exit 1
  fi
fi

if [[ ! -f "$DUMP" ]]; then
  echo "Dump not found: $DUMP" >&2
  exit 1
fi

echo "Starting PostgreSQL 16 in $CONTAINER on 127.0.0.1:$PORT"
$ENGINE rm -f "$CONTAINER" >/dev/null 2>&1 || true
$ENGINE run -d --name "$CONTAINER" \
  -e POSTGRES_USER=stars \
  -e POSTGRES_PASSWORD=stars \
  -e POSTGRES_DB=team_directory \
  -p "$PORT:5432" \
  docker.io/library/postgres:16-alpine >/dev/null

for _ in $(seq 1 30); do
  if $ENGINE exec "$CONTAINER" pg_isready -U stars >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

$ENGINE cp "$DUMP" "$CONTAINER:/tmp/legacy.dump"
$ENGINE exec "$CONTAINER" pg_restore -U stars -d team_directory --no-owner --no-privileges /tmp/legacy.dump

echo
echo "Restored. Run:"
echo "STARS_LEGACY_CONNECTION='Host=127.0.0.1;Port=$PORT;Database=team_directory;Username=stars;Password=stars' \\"
echo "  dotnet run --project tools/Stars.LegacyEval -c Release -- --out=docs/legacy-eval-report.md"
