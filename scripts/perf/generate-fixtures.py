#!/usr/bin/env python3
"""Generate deterministic provider fixtures for STARS performance tests.

Modes:
  auto            distinct cross-provider names -> exercises auto-link + link writes
  near-identical  numeric suffixes with shared tokens -> exercises review/candidate writes

Usage:
  python3 scripts/perf/generate-fixtures.py --mode auto --teams 500 --out /tmp/perf-auto
  python3 scripts/perf/generate-fixtures.py --mode near-identical --teams 1000 --out /tmp/perf-review
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import random
import string
from pathlib import Path

PROVIDER_EVA_ID = 10_000
PROVIDER_REST_ID = 20_000


def team_names(mode: str, count: int, seed: int) -> list[str]:
    if mode == "near-identical":
        return [f"Synthetic Club {index:04d}" for index in range(count)]

    rnd = random.Random(seed)
    codes: set[str] = set()
    while len(codes) < count:
        codes.add("".join(rnd.choices(string.ascii_uppercase + string.digits, k=8)))
    return [f"Perf{code} FC" for code in sorted(codes)]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=["auto", "near-identical"], default="auto")
    parser.add_argument("--teams", type=int, default=500)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    out: Path = args.out
    (out / "eva").mkdir(parents=True, exist_ok=True)
    names = team_names(args.mode, args.teams, args.seed)
    base = dt.datetime(2026, 1, 1, 18, 0, 0, tzinfo=dt.timezone.utc)
    league = "Auto League" if args.mode == "auto" else "Synthetic League"

    entities = [{
        "kind": "competition",
        "key": "competition:perf-league",
        "name": league,
        "sport": "football",
        "country": "RU",
    }]
    rest_events = []
    for index, name in enumerate(names):
        entities.append({
            "kind": "team",
            "key": f"team:rest-{index}",
            "externalId": f"{PROVIDER_REST_ID + index}",
            "name": name,
            "sport": "football",
            "country": "RU",
        })

    for index in range(len(names)):
        kickoff = base + dt.timedelta(days=index // 20, hours=index % 20)
        rest_events.append({
            "key": f"event:rest-{index}",
            "externalId": f"3{index:04d}",
            "sport": "football",
            "competition": "competition:perf-league",
            "kickoff": kickoff.isoformat().replace("+00:00", "Z"),
            "status": "scheduled",
            "participants": [
                {"key": f"team:rest-{index}", "role": "home"},
                {"key": f"team:rest-{(index + 1) % len(names)}", "role": "away"},
            ],
        })

    rows = []
    for index, name in enumerate(names):
        kickoff = base + dt.timedelta(days=index // 20, hours=index % 20)
        rows.append({
            "match_id": 500_000 + index,
            "match_date": kickoff.date().isoformat(),
            "kickoff_time": kickoff.isoformat().replace("+00:00", "Z"),
            "league": league,
            "home_team_id": PROVIDER_EVA_ID + index,
            "home_team_name": name,
            "away_team_id": PROVIDER_EVA_ID + (index + 1) % len(names),
            "away_team_name": names[(index + 1) % len(names)],
            "collected_at": "2026-11-15T00:00:00Z",
        })

    (out / "rest-provider.json").write_text(
        json.dumps({"version": "perf-1", "entities": entities, "events": rest_events}),
        encoding="utf-8",
    )
    (out / "eva" / "api_sport_matches.json").write_text(
        json.dumps({"snapshotId": 101, "rows": rows}),
        encoding="utf-8",
    )

    print(f"mode={args.mode} teams={len(names)} out={out}")
    print(f"  rest entities={len(entities)} events={len(rest_events)}")
    print(f"  eva rows={len(rows)}")


if __name__ == "__main__":
    main()
