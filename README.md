# STARS

A small, self-hosted registry that matches the **same sports team** across independent
providers and keeps the reasoning behind every link. EVA DataHub is one supported source,
not a dependency of the core.

The prototype demonstrates the full path:

```text
EVA DataHub (gRPC/Arrow) ─┐
                          ├─→ Source Registry → local name index → candidates
REST provider (fixture) ──┘                              │
                                                         ├─ auto-link (high confidence)
                                                         └─ review (ambiguous/conflicting)
                                                                 │
                                                    canonical team + audit trail
```

## What is proven

- Two independent connectors feed the same registry: **EVA DataHub** and a **REST/fixture**
  provider.
- `Spartak Moscow` and `Спартак Москва` end up on one canonical team card.
- Ambiguous `Динамо` is **not** merged automatically; it waits in Review.
- Replaying the same snapshot creates no duplicates and does not re-run matching.
- Every active link has an immutable decision, evidence, score and ruleset version.
- Confirmed links are never silently rewritten. A conflict becomes a review item instead.

## Quick start

```bash
docker compose up --build
```

- Admin UI: <http://localhost:5080>
- HTTP API: <http://localhost:5080/api/sources>
- Health: <http://localhost:5080/health/live>, `/health/ready`
- gRPC (cleartext h2c): `http://localhost:5081`

The first startup applies migrations, seeds two demo providers from `fixtures/`, imports
both snapshots and processes the matching queue. No SQL is required.

For local development without Docker:

```bash
podman run -d --name stars-pg -p 5432:5432 \
  -e POSTGRES_USER=stars -e POSTGRES_PASSWORD=stars -e POSTGRES_DB=stars \
  docker.io/library/postgres:18-alpine

cd frontend && npm install && npm run build && cd ..
dotnet run --project src/Stars.Api
```

The API reads the default local connection string from `appsettings.Development.json`.
The Vite dev server (`cd frontend && npm run dev`) proxies `/api` to port 5080.

## Demo walkthrough

1. Open **Sources**. There are two feeds: `EVA DataHub / eva-matches` and
   `API Sport (fixture) / api-sport-snapshot`.
2. Open **Directory**. Four canonical teams are already linked across both providers.
   `Spartak Moscow` has both `Spartak Moscow` (REST) and `Спартак Москва` (EVA) attached.
3. Open **Review**. `Динамо` has two equally plausible candidates
   (`Dynamo Moscow`, `Dinamo Minsk`), so it stays unresolved.
4. Confirm one `Динамо` candidate. A canonical team is created (or reused), the link and
   decision are written in one transaction, and the other pending candidate is superseded.
5. Press **Sync** again. Record counts, links and review candidates do not change.

Useful HTTP calls:

```bash
curl localhost:5080/api/sources
curl localhost:5080/api/directory
curl localhost:5080/api/review
curl localhost:5080/api/metrics

curl -X POST localhost:5080/api/sources/<feedId>/sync
curl -X POST localhost:5080/api/review/<candidateId>/confirm \
  -H 'content-type: application/json' -d '{"reviewer":"alice"}'
curl -X POST localhost:5080/api/review/<candidateId>/reject \
  -H 'content-type: application/json' -d '{"reviewer":"alice","reason":"different club"}'
```

gRPC exposes `ResolveBySourceId`, `GetMasterEntity` and `ListMatchesForReview`
(`src/Stars.Api/Protos/stars.proto`).

## Configuration

| Variable | Default | Meaning |
| --- | --- | --- |
| `ConnectionStrings__Stars` | local dev value | PostgreSQL connection string |
| `Stars__AutoMigrate` | `true` | Apply EF migrations at startup |
| `Stars__MaxRecordsPerSnapshot` | `5000` | Hard ceiling per connector run |
| `Stars__Demo__Enabled` | `true` | Seed demo providers on an empty database |
| `Stars__Demo__AutoSync` | `true` | Run the first demo import before the worker starts |
| `Stars__Demo__FixtureRoot` | `fixtures` | Fixture directory |
| `Stars__Demo__EvaAddress` | empty | Real DataHub address; empty keeps EVA in local fixture mode |
| `Stars__Demo__EvaTokenEnvironmentVariable` | `DATAHUB_SERVICE_TOKEN` | Name of the env var holding the bearer token |
| `DATAHUB_SERVICE_TOKEN` | — | EVA DataHub bearer token, never stored in PostgreSQL |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | empty | Enables the OTLP metrics exporter when set |

Provider secrets are stored only as **references** (`provider_connections.auth_secret_ref`).
`provider_connections.settings` never contains an API key.

## Repository layout

```text
src/
  Stars.Api/                     gRPC + HTTP + hosted worker + static admin UI
  Stars.Application/             normalisation, matching engine, hashes, options
  Stars.Domain/                  EF entities
  Stars.Infrastructure/          Npgsql store, sync, jobs, registry queries
  Stars.Connectors.Abstractions/ provider-neutral connector contract
  Stars.Connectors.Rest/         REST/fixture connector
  Stars.Connectors.Eva/          EVA DataHub gRPC + Arrow connector
  Stars.Api/Protos/stars.proto   public gRPC read API
tests/Stars.Tests/               unit + PostgreSQL/Testcontainers end-to-end tests
fixtures/                        two independent demo snapshots
docs/                            architecture, connector contract, ADRs, prototype report
```

## Safety invariants

- Source identity key is `(source_feed_id, entity_kind, source_key)`. A display name is
  never treated as a global identity.
- `identity_hash` changes only for stable observed fields. Odds snapshots and richer raw
  payloads do not trigger rematching.
- Jobs are idempotent via `(source_entity_id, reason, input_hash, matcher_version)`.
- A source entity has at most one active link (`valid_to IS NULL` partial unique index).
- Providers without a change feed are supported; the EVA connector never schedules
  itself and reads a bounded, pinned snapshot.
- Squad markers (U19/U21, women, reserves) and country/sport conflicts are hard stop
  conditions for automatic linking.
- No OpenSearch, Kafka, Redis, MongoDB, graph database or vector store.

## Tests

```bash
dotnet test tests/Stars.Tests --filter 'FullyQualifiedName!~StarsEndToEndTests'
DOCKER_HOST=unix:///run/user/1000/podman/podman.sock \
TESTCONTAINERS_RYUK_DISABLED=true \
  dotnet test tests/Stars.Tests --filter 'FullyQualifiedName~StarsEndToEndTests'
```

The first command runs pure unit tests. The second starts PostgreSQL with Testcontainers
(`postgres:18-alpine`) and verifies the full demo scenario plus replay idempotency.
On a normal Docker host only `dotnet test tests/Stars.Tests` is needed.

Frontend:

```bash
cd frontend
npm ci
npm run build
```

## Legacy data evaluation

The old Rust `team_directory` dump can be restored into a throwaway PostgreSQL 16
container and evaluated against the new normalizer/matcher:

```bash
./scripts/restore-legacy-dump.sh eva-team-directory_backup_2026-11-15_00-00-08.dump
STARS_LEGACY_CONNECTION='Host=127.0.0.1;Port=55433;Database=team_directory;Username=stars;Password=stars' \
  dotnet run --project tools/Stars.LegacyEval -c Release -- --out=docs/legacy-eval-report.md
```

The dump is not committed. See [Legacy evaluation](docs/legacy-eval.md) for the dataset
shape, current precision/recall baseline and the CI corpus extracted from it.

## Documentation

- [Architecture](docs/architecture.md)
- [PlantUML diagrams](docs/diagrams/README.md): components, deployment, matching algorithm, use cases, request flows, data model
- [Legacy team-directory evaluation](docs/legacy-eval.md)
- [Performance and load testing](docs/performance.md)
- [Connector contract](docs/connector-contract.md)
- [Prototype report](docs/prototype-report.md)
- [ADR-0001: PostgreSQL + pg_trgm](docs/adr/0001-postgres-pg-trgm.md)
- [ADR-0002: no automatic EVA polling](docs/adr/0002-no-automatic-eva-polling.md)
- [ADR-0003: immutable links and review](docs/adr/0003-immutable-links.md)
- [Original prototype plan](STARS_PROTOTYPE_PLAN.md)

## Intentionally out of scope

Players/transfers, LLM or vector matching, ML training, user-supplied DLL plugins,
bidirectional writes to providers, Kubernetes, Kafka, OpenSearch, Redis, MongoDB,
and periodic full S3/Parquet scans.
