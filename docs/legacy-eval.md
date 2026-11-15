# Legacy team-directory evaluation

The old Rust STARS (`team_directory`) dump is a useful labelled corpus. This tool evaluates
the new normalizer and matcher against it without changing the running production database.

## What is inside the dump

Observed from `eva-team-directory_backup_2026-11-15_00-00-08.dump`
(PostgreSQL 16 custom format):

| Table / metric | Value |
| --- | ---: |
| `teams` (canonical teams) | 10,097 |
| `source_teams` (provider records) | 130,813 |
| `source_teams` linked to `team_id` | 26,363 |
| `source_team_names` (name history) | 131,271 |
| `fixtures` (compact match context) | 764,335 |
| confirmed proposals | 10,108 |
| pending proposals | 5,105 |
| provider records without external ID | 75,459 |

Providers: `api-sport-ru` (55,354), `fonbet` (33,522), `archive` (19,643),
`marathon` (14,901), `winline` (7,393). Only the first, second and fourth have confirmed
legacy links.

The important labelled relation is:

```text
source_teams.source_team_id / source
        └── source_teams.team_id  ← canonical team from the old matcher
```

`team_match_proposals.status = 'confirmed'` is a second, pair-level label source.
`fixtures` gives opponents, kickoff and league for context evaluation.

## Restore the dump

```bash
./scripts/restore-legacy-dump.sh eva-team-directory_backup_2026-11-15_00-00-08.dump
```

This starts PostgreSQL 16 on port `55433`, creates `team_directory` and restores the dump.
The dump itself is ignored by git and is not part of the repository.

## Run the evaluation

```bash
STARS_LEGACY_CONNECTION='Host=127.0.0.1;Port=55433;Database=team_directory;Username=stars;Password=stars' \
  dotnet run --project tools/Stars.LegacyEval -c Release -- --out=docs/legacy-eval-report.md
```

The tool reads the legacy database only. It does not write STARS rows and does not require
the STARS API to be running.

What it reports:

1. Dataset counts and provider coverage.
2. New normalizer vs the old Rust normalized names, with common differences.
3. Pair scoring on:
   - **positives** — same legacy `team_id`, different providers;
   - **random negatives** — different teams and providers;
   - **hard negatives** — same new normal form, different legacy teams.
4. A threshold sweep with precision/recall/F1.
5. Context coverage and the same sweep when both sides have fixture context.
6. Ambiguous names that require context/review rather than name-only automation.

## Baseline observed on this dump

The generated full report is [legacy-eval-report.md](legacy-eval-report.md).

With name-only scoring (no sport/country, marker hard stops applied):

| Threshold | Precision | Recall |
| --- | ---: | ---: |
| 0.70 | 99.3% | 92.6% |
| 0.75 | 99.8% | 90.4% |
| 0.80 | 99.9% | 86.1% |
| 0.88 (current default) | 99.98% | 73.6% |

After normalizer corrections for Turkish dotless letters, `ø`, Cyrillic `й/ю/я`,
`до 21` and `St/Sankt`, the normalizer disagreement with the old Rust normalizer dropped
from **11.33%** to **5.96%**, and recall at 0.88 improved from **70.2%** to **73.6%**.

There are **277 ambiguous normalized names** with more than one legacy team
(`al ittihad` 4 teams, `vitoria` 4, `al shabab` 4, `andorra` 3, `bolivar` 3, ...).
These are the cases where equal names must not become identity.

## How to read the numbers

- The dataset is **not a clean benchmark**: only three providers have confirmed links,
  and 104,450 records are unlinked (unknown, not necessarily negative).
- Positives are pairs implied by the same current `team_id`; they include multiple
  provider records and therefore more than the 10,108 confirmed proposals.
- Legacy data has no sport/country columns, so this probe cannot test those components.
- Context is reconstructed from `fixtures` by provider and exact team name. About 92% of
  positive pairs have context on both sides, but context matching currently lowers recall
  when two providers cover different fixtures. Treat context calibration as an open item.
- The old normalized names are an input to the comparison, not a golden standard.

## Recommended next steps

1. Add top-K retrieval evaluation through `pg_trgm` (candidate recall, latency, index size).
2. Calibrate context as a **positive-only boost**; no shared fixture should be neutral, not
   a penalty.
3. Replay the 5,105 legacy pending proposals and compare review routing.
4. Extract a deterministic corpus (positives, random negatives, ambiguous names) for CI,
   so the dump is not needed on every build.
5. Only then run a bounded full import benchmark (e.g. 5–10k source records) through the
   real worker and UI.
