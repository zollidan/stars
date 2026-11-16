# STARS

A small, self-hosted registry that matches the **same sports team** across independent
providers and keeps the reasoning behind every link. EVA DataHub is one supported source,
not a dependency of the core.

```text
EVA DataHub (gRPC/Arrow) ─┐
                          ├─→ Source Registry → local name index → candidates
REST provider (snapshot) ─┘                              │
                                                         ├─ auto-link (high confidence)
                                                         └─ review (ambiguous/conflicting)
                                                                 │
                                                    canonical team + audit trail
```

## What it guarantees

- Two independent connectors can feed the same registry: **EVA DataHub** and any provider
  exposing the documented **REST snapshot** shape.
- `Spartak Moscow` and `Спартак Москва` end up on one canonical team card.
- Ambiguous `Динамо` is **not** merged automatically; it waits in Review.
- Replaying the same snapshot creates no duplicates and does not re-run matching.
- Every active link has an immutable decision, evidence, score and ruleset version.
- Confirmed links are never silently rewritten. A conflict becomes a review item instead.

## Quick start

```bash
STARS_EVA_ADDRESS=http://datahub:50051 \
DATAHUB_SERVICE_TOKEN=... \
docker compose up --build
```

- Admin UI: <http://localhost:5080>
- HTTP API: <http://localhost:5080/api/sources>
- Interactive HTTP API reference: <http://localhost:5080/scalar>
- OpenAPI document: <http://localhost:5080/openapi/v1.json>
- Health: <http://localhost:5080/health/live>, `/health/ready`, `/health/db`
- gRPC (cleartext h2c): `http://localhost:5081`

The first startup applies migrations and reconciles providers and feeds declared under
`Stars:Providers`. There is no demo data and no automatic import: open **Sources** and
press **Sync**, or call `POST /api/sync`, when you want a bounded snapshot.

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

## Provider configuration

Providers and feeds are declared once in configuration and reconciled on startup. Existing
cursors, links and sync history are never changed by reconciliation. The shipped
Development and Production settings define one EVA DataHub provider; override any value
with environment variables in production.

```json
{
  "Stars": {
    "Providers": {
      "eva": {
        "Name": "EVA DataHub",
        "ConnectorKey": "eva.datahub",
        "BaseUrl": "http://datahub:50051",
        "AuthSecretRef": "DATAHUB_SERVICE_TOKEN",
        "Enabled": true,
        "Settings": {
          "dataset": "api_sport_matches_v1"
        },
        "Feeds": {
          "api-sport": {
            "EntityKind": "match",
            "DatasetRef": "api_sport_matches_v1",
            "SyncMode": "snapshot",
            "Enabled": true,
            "Config": {}
          }
        }
      }
    }
  }
}
```

Useful environment overrides:

```bash
Stars__Providers__eva__BaseUrl=http://datahub:50051
Stars__Providers__eva__Settings__dataset=api_sport_matches_v1
DATAHUB_SERVICE_TOKEN=...
```

Add one entry under `Feeds` for each EVA dataset (`api_sport_matches_v1`, `odds_v1`,
`winline_odds_v1`, `marathon_odds_v1`). A feed's `Config.dataset` overrides
`Settings.dataset`, so one DataHub connection and token can serve several feeds. See the
[schema mappings and identity limits](docs/connector-contract.md#6-eva-datahub-connector).

A REST provider is configured the same way with connector key `rest.snapshot`. It calls
`GET {BaseUrl}/snapshot`; see the
[REST snapshot contract](docs/connector-contract.md#7-rest-snapshot-connector).

Provider secrets are stored only as **references** (`provider_connections.auth_secret_ref`).
`provider_connections.settings` never contains an API key.

## Connecting a real DataHub

The EVA connector talks to DataHub over gRPC and reads Arrow batches. The compose file
can run a read-side DataHub image pulled from GHCR:

```bash
cp .env.example .env
# Fill DATAHUB_SERVICE_TOKEN, DATAHUB_WRITE_CATALOG_URL, LAKE_S3_BUCKET and S3 credentials.
echo "$GHCR_TOKEN" | docker login ghcr.io -u <github-user> --password-stdin
docker compose --profile datahub up --build
```

The `datahub` service uses `ghcr.io/zollidan/eva-datahub` and expects an existing EVA
DuckLake catalog and S3 bucket. It is intentionally read-only for STARS: STARS only calls
`ReadDataset` and never writes to the lake. Use a catalog copy or a dedicated catalog
database for a read-side instance, and prefer credentials limited to reading the lake.

If you already have a running DataHub, skip the profile and point STARS at it:

```bash
STARS_EVA_ADDRESS=http://datahub:50051 \
DATAHUB_SERVICE_TOKEN=... \
docker compose up --build
```

The GHCR package is private. Use a token with `read:packages` (or the GitHub Actions
`GITHUB_TOKEN` with package access). Pin `DATAHUB_VERSION` to a published tag for
repeatable deployments.

A bounded sync reads at most `Stars__MaxRecordsPerSnapshot` input rows per run. If the
dataset is larger, the EVA cursor stays resumable and the next sync continues from the
next offset instead of starting over.

## HTTP API

Useful calls:

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

The admin UI and API have no built-in authentication. Deploy behind a trusted network or
an authenticating reverse proxy.

## Container image and GitHub Actions

[CI](.github/workflows/ci.yml) builds and tests the backend (including PostgreSQL
integration tests), tests/builds the frontend, then builds the complete container image.
Pull requests only build; pushes to `master`/`main`, tags matching `v*`, and manual
workflow runs also publish to `ghcr.io/zollidan/stars` using `GITHUB_TOKEN` with
`packages: write`. No registry password secret is required.

Image tags:

- `latest` — the latest successful build of the repository's default branch.
- `sha-<full-commit-sha>` — a specific revision.
- `v1.2.3` — a build triggered by that Git tag.

Images target `linux/amd64`. For repeatable deployments, use a version tag or image digest.
In another project's Compose file, connect STARS to an existing PostgreSQL database:

```yaml
services:
  stars:
    image: ghcr.io/zollidan/stars:latest
    environment:
      ConnectionStrings__Stars: Host=postgres;Database=stars;Username=stars;Password=${STARS_DB_PASSWORD}
      Stars__Providers__eva__BaseUrl: http://datahub:50051
      DATAHUB_SERVICE_TOKEN: ${DATAHUB_SERVICE_TOKEN}
    ports:
      - "5080:5080"
      - "5081:5081"
```

The database and credentials must already exist; STARS applies its schema migrations at
startup. The image includes the admin UI and Scalar at `/scalar`, with OpenAPI JSON at
`/openapi/v1.json`, in all environments. Scalar covers the HTTP API; the gRPC contract
remains [stars.proto](src/Stars.Api/Protos/stars.proto). Scalar's JavaScript is served
locally, and external font loading is disabled.

New GHCR packages are private by default. For another repository's Actions workflow to
pull a private image, grant that repository read access under the package's **Manage
Actions access** settings and use its `GITHUB_TOKEN` with `packages: read`. For a local
or external deployment, authenticate to GHCR with a token that has `read:packages`, or
make the package public if anonymous pulls are intended.

## Configuration

| Variable | Default | Meaning |
| --- | --- | --- |
| `ConnectionStrings__Stars` | local dev value | PostgreSQL connection string |
| `Stars__AutoMigrate` | `true` | Apply EF migrations and provider configuration at startup |
| `Stars__MaxRecordsPerSnapshot` | `5000` | Hard ceiling on input snapshot records per run (match rows for EVA) |
| `Stars__WorkerPollMilliseconds` | `750` | Idle polling interval of the matching worker |
| `Stars__JobBatchSize` | `4` | Matching jobs processed per worker iteration |
| `Stars__Providers__<key>__*` | see above | Declarative provider and feed definitions |
| `DATAHUB_SERVICE_TOKEN` | — | EVA DataHub bearer token, never stored in PostgreSQL |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | empty | Enables the OTLP metrics exporter when set |

Provider settings may also set `maxRecords` to lower the global snapshot ceiling for one
provider. The sync service rejects oversized pages instead of truncating them.

The EVA connector reads API-Sport (`api_sport_matches_v1`), Fonbet (`odds_v1`),
Winline (`winline_odds_v1`) and Marathon (`marathon_odds_v1`) from the same DataHub.

## Repository layout

```text
src/
  Stars.Api/                     gRPC + HTTP + hosted worker + static admin UI
  Stars.Application/             normalisation, matching engine, hashes, options
  Stars.Domain/                  EF entities
  Stars.Infrastructure/          Npgsql store, sync, jobs, registry queries, provider bootstrap
  Stars.Connectors.Abstractions/ provider-neutral connector contract
  Stars.Connectors.Rest/         REST snapshot connector
  Stars.Connectors.Eva/          EVA DataHub gRPC + Arrow connector
  Stars.Api/Protos/stars.proto   public gRPC read API
tests/Stars.Tests/               unit + PostgreSQL/Testcontainers end-to-end tests
docs/                            architecture, connector contract, ADRs, evaluation
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
(`postgres:18-alpine`) and verifies two independent providers, replay idempotency,
matching, review and the gRPC read API. On a normal Docker host only
`dotnet test tests/Stars.Tests` is needed.

Frontend:

```bash
cd frontend
npm ci
npm test
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
- [ADR-0001: PostgreSQL + pg_trgm](docs/adr/0001-postgres-pg-trgm.md)
- [ADR-0002: no automatic EVA polling](docs/adr/0002-no-automatic-eva-polling.md)
- [ADR-0003: immutable links and review](docs/adr/0003-immutable-links.md)

## Intentionally out of scope

Players/transfers, LLM or vector matching, ML training, user-supplied DLL plugins,
bidirectional writes to providers, Kubernetes, Kafka, OpenSearch, Redis, MongoDB,
and periodic full S3/Parquet scans.
