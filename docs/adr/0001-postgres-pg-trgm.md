# ADR-0001: PostgreSQL + pg_trgm for registry and name search

- Status: accepted
- Date: 2026-11-15
- Context: product v1

## Decision

Store source observations, canonical entities, links, evidence, decisions, jobs and
cursors in one PostgreSQL database. Use the `pg_trgm` extension with GIN indexes for local
fuzzy name search. Use Npgsql parameterised SQL for search and batch upserts, and EF Core
for schema/migrations and simple reads.

## Consequences

- One operational dependency; no OpenSearch, Redis, MongoDB, Kafka or graph database.
- Candidate search stays in the database and does not load all names into process memory.
- Trigram recall is sufficient for current data volumes. A later move to a dedicated search
  engine is possible without changing the connector or registry contracts.
- `pg_trgm` is created by an EF migration and required at startup.
