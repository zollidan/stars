# Connector contract (v0.1)

A connector is the only place that knows a provider. It depends on
`Stars.Connectors.Abstractions` and never on the registry, matching engine or database.

## 1. Interface

```csharp
public interface IConnector
{
    string Key { get; }
    ConnectorCapabilities Capabilities { get; }

    IAsyncEnumerable<SourceBatch> ReadSnapshotAsync(
        ConnectorContext context,
        CancellationToken cancellationToken);
}
```

The host resolves a connector by `provider_connections.connector_key`. Registration is
done in the composition root (`Stars.Api/Program.cs`); adding a provider does not require
changes in `Stars.Application`, `Stars.Core` equivalents or matching code.

## 2. Capabilities

```csharp
public sealed record ConnectorCapabilities(
    bool SupportsSnapshot,
    bool SupportsChanges,
    bool SupportsCursor,
    bool SupportsPush,
    string SyncMode,
    string? Notes = null);
```

- **snapshot** — the only mode required by the prototype.
- **changes** — optional future `ReadChanges`.
- **cursor** — a stable resume token for a bounded read or pinned snapshot.
- **push** — optional future webhook/spool.

A provider without change support is a normal, fully supported connector. STARS never
invents a change feed by polling a full S3/Parquet archive.

## 3. Records

```csharp
SourceEntityRecord(
    string EntityKind,       // "team", "competition", "player"
    string SourceKey,        // stable within (feed, kind)
    string? ExternalId,      // provider id when it exists
    string DisplayName,
    string? SportCode,
    string? CountryCode,
    IReadOnlyList<SourceNameRecord>? Names,
    JsonElement? Attributes,
    DateTimeOffset? ObservedAt)

SourceEventRecord(
    string SourceKey,
    string? ExternalId,
    string? SportCode,
    string? CompetitionSourceKey,
    DateTimeOffset? KickoffAt,
    string? Status,
    IReadOnlyList<SourceEventParticipantRecord> Participants,
    JsonElement? Attributes,
    DateTimeOffset? ObservedAt)
```

Rules:

- `SourceKey` must remain stable while the provider means the same real-world entity. If
  there is no native ID, the connector is responsible for deriving a stable key (for
  example a provider-scoped slug plus competition context). A display name alone is not
  enough for a global identity.
- Participant keys refer to entities in the same feed. Include those entities in the same
  or an earlier batch.
- `Attributes` is a small JSON context, never a raw odds history or full provider payload.
- `ObservedAt` is when the provider observed the record. If a delayed observation arrives
  after a newer one, STARS keeps the newer content and only advances `last_seen_at`. When
  omitted, ingestion uses the current time.

## 4. Batching and checkpoints

```csharp
SourceBatch(
    IReadOnlyList<SourceEntityRecord> Entities,
    IReadOnlyList<SourceEventRecord> Events,
    string? Checkpoint,
    long Sequence)
```

- The connector streams pages; it must not materialise the whole provider dataset.
- `Checkpoint` is written to `sync_cursors` only after every record in the page is
  committed. A connector that cannot resume should return a new checkpoint per page and
  tolerate replay.
- `RecordCount` counts entities plus events.
- `ConnectorContext.MaxRecords` is a hard ceiling. Implementations must stop before
  exceeding it.

## 5. Context and secrets

```csharp
public sealed record ConnectorContext(
    Guid ProviderConnectionId,
    Guid SourceFeedId,
    string FeedKey,
    string EntityKind,
    string? BaseUrl,
    JsonElement Settings,   // provider settings merged with feed config
    string? Cursor,
    long MaxRecords,
    string? Secret,         // already resolved by the host
    HttpClient? HttpClient);
```

Only a secret **reference** is stored in `provider_connections.auth_secret_ref`. The host
resolves the reference from an environment variable (`STARS_SECRET_<ref>` or `<ref>`).
The connector receives the resolved value for the duration of the run.

## 6. EVA DataHub connector

Key: `eva.datahub`.

Settings:

```json
{
  "mode": "grpc",
  "address": "http://datahub:50051",
  "dataset": "api_sport_matches_v1",
  "pageSize": 1000,
  "maxRecords": 300
}
```

The gRPC path:

1. Opens `DataHubService.DataHubServiceClient` with a bearer token.
2. Calls `ReadDataset` with `query_json` containing a bounded `limit`, `offset`, an
   explicit column list and deterministic ordering.
3. Pins `snapshot_id` when resuming an interrupted run; a completed cursor starts a fresh
   snapshot.
4. Decodes each `DatasetBatch.arrow_ipc` with an Arrow IPC stream reader.
5. Reduces rows to teams, competitions and one compact event per match. **Odds and
   bookmaker snapshots are not stored.**
6. Emits a checkpoint `eva:<dataset>:<snapshot>:<dataset_snapshot>:<offset>`.

`ReadChanges` is absent from the current DataHub contract. The connector advertises
`SupportsChanges = false` and is never put on a timer. A manual import or a bounded
administrative run is required. This is deliberate (ADR-0002).

For development, `mode = "fixture"` reads an EVA-shaped JSON file through the same row
mapping. It exists to exercise the EVA dataset shape without a DataHub server; the gRPC
path remains the production path.

## 7. REST/fixture connector

Key: `rest.fixture`.

Settings:

```json
{
  "fixturePath": "/app/fixtures/rest-provider.json",
  "pageSize": 100,
  "snapshotVersion": "2026-11-14"
}
```

If `fixturePath` is absent, the connector calls `GET {baseUrl}/snapshot` with the shared
`HttpClient`.

Snapshot shape:

```json
{
  "version": "2026-11-14",
  "entities": [
    {
      "kind": "team",
      "key": "team:spartak-msk",
      "externalId": "100",
      "name": "Spartak Moscow",
      "sport": "football",
      "country": "RU",
      "aliases": ["FC Spartak"],
      "attributes": { "stadium": "Lukoil Arena" }
    }
  ],
  "events": [
    {
      "key": "event:1001",
      "externalId": "1001",
      "sport": "football",
      "competition": "competition:rpl",
      "kickoff": "2026-11-20T18:00:00Z",
      "status": "scheduled",
      "participants": [
        { "key": "team:spartak-msk", "role": "home" },
        { "key": "team:zenith", "role": "away" }
      ]
    }
  ]
}
```

Entities are emitted before events so event participants always resolve. Checkpoints are
`<version>:entities:<page>`, `<version>:events:<page>` and end with `<version>:complete`.

## 8. Adding a provider

1. Create a class library referencing `Stars.Connectors.Abstractions`.
2. Implement `IConnector`.
3. Register it once in the composition root.
4. Add a `provider_connections` row and a `source_feeds` row, or extend the demo seeder.
5. Add connector-level mapping tests and an end-to-end fixture.

No changes to the Source Registry, matching engine or API are required.
