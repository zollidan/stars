# Connector contract (v1)

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

- **snapshot** — the baseline sync mode.
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
    long Sequence = 0,
    int? InputRecordCount = null)
```

- The connector streams pages; it must not materialise the whole provider dataset.
- `Checkpoint` is written to `sync_cursors` only after every record in the page is
  committed. A connector that cannot resume should return a new checkpoint per page and
  tolerate replay.
- `RecordCount` counts input snapshot records. By default this is entities plus events.
  A connector that expands one input row into several observations sets `InputRecordCount`;
  EVA counts match rows, including the teams and competition produced from each row.
- `ConnectorContext.MaxRecords` is a hard ceiling. Implementations must stop before
  exceeding it. Provider/feed `maxRecords` can lower the global limit. The sync service
  rejects oversized pages without committing their checkpoint; it never truncates a page.

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

The `dataset` setting selects one of the supported EVA schemas:

| Source | Dataset | Team names | Event identity | Observation time |
| --- | --- | --- | --- | --- |
| API-Sport | `api_sport_matches_v1` | `home_team_name`, `away_team_name` (with team IDs) | `match_id` | `collected_at`, falling back to `first_seen_at` |
| Fonbet | `odds_v1` | `team1`, `team2` | `event_id` | `last_seen_at`, falling back to `first_seen_at` |
| Winline | `winline_odds_v1` | `team1`, `team2` | `event_id` | `last_seen_at`, falling back to `first_seen_at` |
| Marathon | `marathon_odds_v1` | `team1`, `team2` | `event_id` | `last_seen_at`, falling back to `first_seen_at` |

Bookmaker schemas use `start_time` for kickoff; Winline and Marathon also provide an
optional `league`. Fonbet has no league field, so the connector does not request it.
Only identity/context columns are requested: prices, totals and historical
`*_snapshots_v1` datasets are excluded.

Create a separate feed for each dataset. A feed's `config.dataset` overrides the
provider's `settings.dataset`, so the same DataHub connection/token can serve all four
feeds. `dataset_ref` is descriptive metadata; `config.dataset` selects the actual read.
Existing feeds are not created or modified automatically by adding schema support.

API-Sport keeps `team:<team_id>` source keys. Bookmakers have no team IDs: their keys
contain the lowercased, trimmed, URI-escaped name and, when supplied, league. For example,
`team:name:alpha:league:premier%20league`. Without league the key is `team:name:alpha`.
Keys are scoped to the feed. Homonyms without league cannot be distinguished, and a
name/league change creates another source identity; provider team IDs are needed to
remove this limitation. `event_id` is never used as a team ID.

Settings:

```json
{
  "address": "http://datahub:50051",
  "dataset": "api_sport_matches_v1",
  "maxRecords": 300
}
```

For Winline, use `"dataset": "winline_odds_v1"`; for Fonbet, `"odds_v1"`; for Marathon,
`"marathon_odds_v1"`. The address and secret reference stay the same.

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

## 7. REST snapshot connector

Key: `rest.snapshot`.

Settings:

```json
{
  "pageSize": 100
}
```

The connector calls `GET {baseUrl}/snapshot` with the shared `HttpClient`. The provider
must return the documented snapshot shape and a stable source key for every entity and
event. Replays are idempotent because keys do not change.

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
4. Add the provider and its feeds under `Stars:Providers` in configuration.
5. Add connector-level mapping tests and an end-to-end test.

No changes to the Source Registry, matching engine or API are required.
