# ADR-0002: no automatic EVA DataHub polling

- Status: accepted
- Date: 2026-11-15
- Context: product v1

## Decision

The EVA connector advertises `SupportsChanges = false`. It reads a bounded snapshot only
when an operator requests it (or an explicit administrative job does). It is not placed on
a periodic timer and it does not scan the full S3/Parquet archive.

The snapshot is resumable inside a pinned `snapshot_id` using `limit`/`offset` and a
per-page cursor. A completed cursor starts a fresh snapshot on the next manual run, so
new data is visible and replay remains idempotent.

## Consequences

- The connector does not pretend a change feed exists.
- Freshness for EVA is explicit and observable in `sync_runs`.
- Other providers may supply incremental context through their own contracts; STARS does
  not simulate a change feed by reading a local file.
- Any future polling must be justified by a bounded, server-supported change API, not by
  walking storage.
