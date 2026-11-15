# Prototype report (v0.1)

Date: 2026-11-15  
Environment: local Linux, PostgreSQL 18 in a container, ASP.NET Core 10 (Debug build),
demo fixtures from `fixtures/`.

## 1. What was exercised

- `rest.fixture` connector reading `fixtures/rest-provider.json` (8 teams, 1 competition,
  4 fixtures).
- `eva.datahub` connector in EVA-shaped local fixture mode reading
  `fixtures/eva/api_sport_matches.json` (4 matches, 8 teams, 2 competitions). The same
  connector contains the real gRPC `ReadDataset` + Arrow path; fixture mode only replaces
  the transport for a runnable demo.
- Source ingestion, two-pass normalization, `pg_trgm` candidate search, scoring, automatic
  linking, review routing, manual API operations, idempotent replay and gRPC reads.

## 2. Demo outcome

| Metric | Value |
| --- | --- |
| Providers | 2 |
| Source feeds | 2 |
| Source entity records | 16 |
| Source events | 8 |
| Canonical teams | 4 |
| Active links | 8 |
| Pending review candidates | 4 |
| Failed sync runs | 0 |

Canonical links produced:

| Canonical | REST source | EVA source |
| --- | --- | --- |
| CSKA Moscow | CSKA Moscow | ЦСКА |
| Rubin Kazan | Rubin Kazan | Рубин |
| Spartak Moscow | Spartak Moscow | Спартак Москва |
| Zenit St Petersburg | Zenit St Petersburg | Зенит Санкт-Петербург |

`Динамо` stayed unresolved. The EVA record `Динамо` had two equally scored candidates
(`Dynamo Moscow`, `Dinamo Minsk`) and was routed to Review. There were **no false merges**
in the demo set.

## 3. Replay / idempotency

Two consecutive `POST /api/sync` runs after the initial import:

- every feed returned `recordsChanged: 0`;
- source entities stayed at 16, events at 8;
- masters stayed at 4, active links at 8, pending reviews at 4;
- no new processing jobs were enqueued (`pendingJobs: 0`, `doneJobs` unchanged at 23);
- the cursor advanced only because the connector produced a completed-snapshot checkpoint.

This is the required "same snapshot twice does not rematch or duplicate" behaviour.

## 4. Basic measurements (Debug build, local)

| Metric | Value |
| --- | --- |
| Records read across both feeds | 24 |
| Average sync duration | ~0.10 s |
| PostgreSQL database size after demo | ~9.2 MB |
| API process RSS (Debug) | ~179 MB |
| Failed sync runs | 0 |

CPU was not benchmarked. For a meaningful number use `dotnet run -c Release`, a fixed
machine, and `docker stats` / `pidstat`. The prototype's target is correctness and bounded
I/O, not throughput.

## 5. Quality checks covered by tests

- `Spartak Moscow` ↔ `Спартак Москва` (transliteration + shared fixture context).
- `Динамо` ambiguous → review; not merged.
- U19 vs senior → hard conflict.
- Known country conflict → hard conflict.
- One-token name without context → review.
- Shared context absence → below-threshold review.
- Rename → new identity hash; volatile attributes → no identity change.
- Replay → no duplicate rows, links or candidates.
- gRPC `ResolveBySourceId` and `GetMasterEntity` against the seeded database.
- Connectors stream pages, honour the record ceiling and produce stable checkpoints.

## 6. Limits of this measurement

- EVA was run through its fixture transport, not against a live DataHub instance. The gRPC
  request builder and Arrow decoding are covered by code paths and connector tests but a
  live DataHub run remains an operator step.
- The admin UI has no authentication; it is a local/trusted-network tool.
- The demo is tiny. `pg_trgm` behaviour on hundreds of thousands of names, index bloat,
  long-running snapshot memory and multi-worker contention remain to be measured.
- The worker is a single process. Queue leasing already supports more workers, but no
  stress test has been performed.

## 7. Legacy dump evaluation

The old Rust `team_directory` dump was restored separately and used as a labelled corpus:

- 130,813 provider records, 26,363 with a legacy `team_id`;
- 10,097 canonical teams and 22,398 cross-provider positive pairs;
- 50,000 random cross-provider negatives and 277 ambiguous names as hard negatives.

Name-only scoring at the current 0.88 threshold reached **99.98% precision / 73.6%
recall** on positives. At 0.70 it reached **99.3% / 92.6%**. The dump also exposed real
normalizer defects (Turkish dotless letters, `ø`, Cyrillic `й/ю/я`, `до 21`,
`St/Sankt`), which were fixed; disagreement with the old normalizer fell from 11.33% to
5.96%. Details: [legacy-eval.md](legacy-eval.md).

## 8. Reproduce

```bash
docker compose up --build
curl localhost:5080/api/metrics
curl localhost:5080/api/directory
curl localhost:5080/api/review
curl -X POST localhost:5080/api/sync
curl localhost:5080/api/metrics
```

Point the EVA connection at a live DataHub:

```bash
export STARS_EVA_ADDRESS=http://datahub:50051
export DATAHUB_SERVICE_TOKEN=...
docker compose up --build
```

The seeded EVA connection switches to gRPC when `Stars__Demo__EvaAddress` is set.
