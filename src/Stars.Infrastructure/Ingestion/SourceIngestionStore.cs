using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Stars.Application.Ingestion;
using Stars.Application.Matching;
using Stars.Application.Normalization;
using Stars.Connectors.Abstractions;

namespace Stars.Infrastructure.Ingestion;

public sealed record FeedDescriptor(
    Guid SourceFeedId,
    string FeedKey);

public sealed record BatchIngestionResult(
    int EntitiesNew,
    int EntitiesChanged,
    int EventsNew,
    int EventsChanged,
    int JobsEnqueued);

/// <summary>
/// Writes provider observations into the Source Registry. All statements in one batch
/// run in a single transaction together with the cursor advance, so a crash can at worst
/// replay a batch; it can never advance the cursor past uncommitted data.
/// </summary>
public sealed class SourceIngestionStore(
    NpgsqlDataSource dataSource,
    ILogger<SourceIngestionStore> logger)
{
    public async Task<BatchIngestionResult> IngestBatchAsync(
        FeedDescriptor feed,
        SourceBatch batch,
        string? checkpoint,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var entityIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var changedEntityIds = new HashSet<Guid>();
            var contextHashes = new Dictionary<Guid, HashSet<string>>();
            var newEntities = 0;
            var changedEntities = 0;
            var jobs = 0;

            foreach (var entity in batch.Entities)
            {
                var result = await UpsertEntityAsync(connection, transaction, feed, entity, cancellationToken);
                entityIds[entity.SourceKey] = result.Id;
                newEntities += result.IsNew ? 1 : 0;
                changedEntities += result.Changed ? 1 : 0;
                if (result.Changed)
                {
                    changedEntityIds.Add(result.Id);
                    jobs += await EnqueueJobAsync(
                        connection,
                        transaction,
                        result.Id,
                        "identity",
                        IdentityHasher.Entity(entity),
                        cancellationToken);
                }
            }

            var newEvents = 0;
            var changedEvents = 0;
            foreach (var sourceEvent in batch.Events)
            {
                var result = await UpsertEventAsync(
                    connection,
                    transaction,
                    feed,
                    sourceEvent,
                    entityIds,
                    cancellationToken);
                newEvents += result.IsNew ? 1 : 0;
                changedEvents += result.Changed ? 1 : 0;

                if (result.Changed)
                {
                    foreach (var participantId in result.ParticipantEntityIds)
                    {
                        if (changedEntityIds.Contains(participantId))
                        {
                            // The identity job that is already queued will observe the
                            // new event context. Do not enqueue a duplicate job.
                            continue;
                        }

                        if (!contextHashes.TryGetValue(participantId, out var hashes))
                        {
                            contextHashes[participantId] = hashes = [];
                        }

                        hashes.Add(result.ContextHash);
                    }
                }
            }

            // One context job per entity per batch, keyed by all changed event hashes.
            // A replay produces the same key and inserts nothing.
            foreach (var (participantId, hashes) in contextHashes)
            {
                var combined = IdentityHasher.Job(
                    "event",
                    string.Join('|', hashes.Order(StringComparer.Ordinal)));
                jobs += await EnqueueJobAsync(
                    connection,
                    transaction,
                    participantId,
                    "event_context",
                    combined,
                    cancellationToken);
            }

            await AdvanceCursorAsync(connection, transaction, feed.SourceFeedId, checkpoint, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new BatchIngestionResult(
                newEntities,
                changedEntities,
                newEvents,
                changedEvents,
                jobs);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<UpsertEntityResult> UpsertEntityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FeedDescriptor feed,
        SourceEntityRecord entity,
        CancellationToken cancellationToken)
    {
        var normalizedName = NameNormalizer.Normalize(entity.DisplayName);
        var identityHash = IdentityHasher.Entity(entity);

        var observedAt = (entity.ObservedAt ?? DateTimeOffset.UtcNow).ToUniversalTime();

        Guid id;
        string? existingHash;
        DateTimeOffset? lastSeen = null;
        await using (var select = new NpgsqlCommand(
            """
            SELECT id, identity_hash, last_seen_at
            FROM source_entities
            WHERE source_feed_id = @feed AND entity_kind = @kind AND source_key = @key
            FOR UPDATE
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("feed", feed.SourceFeedId);
            select.Parameters.AddWithValue("kind", entity.EntityKind);
            select.Parameters.AddWithValue("key", entity.SourceKey);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                id = reader.GetGuid(0);
                existingHash = reader.GetString(1);
                lastSeen = reader.GetFieldValue<DateTimeOffset>(2);
            }
            else
            {
                id = Guid.Empty;
                existingHash = null;
            }
        }

        var isNew = id == Guid.Empty;
        var changed = isNew || !string.Equals(existingHash, identityHash, StringComparison.Ordinal);
        var stale = lastSeen is not null && observedAt < lastSeen;
        var applied = changed && !stale;

        if (isNew)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO source_entities
                    (source_feed_id, entity_kind, source_key, external_id, display_name,
                     normalized_name, sport_code, country_code, attributes, identity_hash,
                     status, first_seen_at, last_seen_at)
                VALUES
                    (@feed, @kind, @key, @external, @name, @normalized, @sport, @country,
                     @attributes, @hash, 'new', @observed, @observed)
                RETURNING id
                """, connection, transaction);
            AddEntityParameters(insert, feed, entity, normalizedName, identityHash, observedAt);
            id = (Guid)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }
        else if (applied)
        {
            await using var update = new NpgsqlCommand(
                """
                UPDATE source_entities
                SET external_id = @external,
                    display_name = @name,
                    normalized_name = @normalized,
                    sport_code = @sport,
                    country_code = @country,
                    attributes = @attributes,
                    identity_hash = @hash,
                    status = 'changed',
                    last_seen_at = @observed
                WHERE id = @id
                """, connection, transaction);
            AddEntityParameters(update, feed, entity, normalizedName, identityHash, observedAt);
            update.Parameters.AddWithValue("id", id);
            await update.ExecuteNonQueryAsync(cancellationToken);

            await using var closeNames = new NpgsqlCommand(
                "UPDATE source_entity_names SET is_current = false WHERE source_entity_id = @id AND is_current",
                connection, transaction);
            closeNames.Parameters.AddWithValue("id", id);
            await closeNames.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var touch = new NpgsqlCommand(
                """
                UPDATE source_entities
                SET last_seen_at = GREATEST(last_seen_at, @observed),
                    status = CASE WHEN status = 'new' THEN 'active' ELSE status END
                WHERE id = @id
                """, connection, transaction);
            touch.Parameters.AddWithValue("id", id);
            touch.Parameters.AddWithValue("observed", observedAt);
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }

        if (stale)
        {
            logger.LogDebug(
                "Ignoring stale observation of {EntityKey} at {ObservedAt}; newest is {LastSeen}",
                entity.SourceKey,
                observedAt,
                lastSeen);
            return new UpsertEntityResult(id, isNew, false);
        }

        await UpsertNameAsync(connection, transaction, id, entity.DisplayName, normalizedName, null, true, observedAt, cancellationToken);
        foreach (var alias in entity.Names ?? [])
        {
            if (string.IsNullOrWhiteSpace(alias.Name)
                || string.Equals(alias.Name.Trim(), entity.DisplayName.Trim(), StringComparison.Ordinal))
            {
                continue;
            }

            await UpsertNameAsync(
                connection,
                transaction,
                id,
                alias.Name.Trim(),
                NameNormalizer.Normalize(alias.Name),
                alias.LanguageCode,
                false,
                observedAt,
                cancellationToken);
        }

        return new UpsertEntityResult(id, isNew, applied);
    }

    private static void AddEntityParameters(
        NpgsqlCommand command,
        FeedDescriptor feed,
        SourceEntityRecord entity,
        string normalizedName,
        string identityHash,
        DateTimeOffset observedAt)
    {
        command.Parameters.AddWithValue("feed", feed.SourceFeedId);
        command.Parameters.AddWithValue("kind", entity.EntityKind);
        command.Parameters.AddWithValue("key", entity.SourceKey);
        command.Parameters.Add(new NpgsqlParameter("external", NpgsqlDbType.Text)
        {
            Value = (object?)entity.ExternalId ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("name", entity.DisplayName.Trim());
        command.Parameters.AddWithValue("normalized", normalizedName);
        command.Parameters.Add(new NpgsqlParameter("sport", NpgsqlDbType.Text)
        {
            Value = (object?)entity.SportCode ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("country", NpgsqlDbType.Text)
        {
            Value = (object?)entity.CountryCode?.ToUpperInvariant() ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("attributes", NpgsqlDbType.Jsonb)
        {
            Value = entity.Attributes?.GetRawText() ?? "{}",
        });
        command.Parameters.AddWithValue("hash", identityHash);
        command.Parameters.AddWithValue("observed", observedAt);
    }

    private static async Task UpsertNameAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceEntityId,
        string name,
        string normalizedName,
        string? languageCode,
        bool isCurrent,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO source_entity_names
                (source_entity_id, name, normalized_name, language_code, first_seen_at, last_seen_at, is_current)
            VALUES
                (@id, @name, @normalized, @language, @observed, @observed, @current)
            ON CONFLICT (source_entity_id, name) DO UPDATE
            SET last_seen_at = GREATEST(source_entity_names.last_seen_at, EXCLUDED.last_seen_at),
                is_current = EXCLUDED.is_current,
                language_code = COALESCE(EXCLUDED.language_code, source_entity_names.language_code)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", sourceEntityId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("normalized", normalizedName);
        command.Parameters.AddWithValue("observed", observedAt);
        command.Parameters.Add(new NpgsqlParameter("language", NpgsqlDbType.Text)
        {
            Value = (object?)languageCode ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("current", isCurrent);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<UpsertEventResult> UpsertEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FeedDescriptor feed,
        SourceEventRecord sourceEvent,
        Dictionary<string, Guid> batchEntities,
        CancellationToken cancellationToken)
    {
        var competition = await ResolveCompetitionAsync(
            connection, transaction, feed.SourceFeedId, sourceEvent.CompetitionSourceKey,
            batchEntities, cancellationToken);
        var contextHash = IdentityHasher.Event(sourceEvent, competition.NormalizedName);

        var observedAt = (sourceEvent.ObservedAt ?? DateTimeOffset.UtcNow).ToUniversalTime();

        Guid id;
        string? existingHash;
        DateTimeOffset? lastSeen = null;
        await using (var select = new NpgsqlCommand(
            """
            SELECT id, context_hash, last_seen_at
            FROM source_events
            WHERE source_feed_id = @feed AND source_key = @key
            FOR UPDATE
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("feed", feed.SourceFeedId);
            select.Parameters.AddWithValue("key", sourceEvent.SourceKey);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                id = reader.GetGuid(0);
                existingHash = reader.GetString(1);
                lastSeen = reader.GetFieldValue<DateTimeOffset>(2);
            }
            else
            {
                id = Guid.Empty;
                existingHash = null;
            }
        }

        var isNew = id == Guid.Empty;
        var changed = isNew || !string.Equals(existingHash, contextHash, StringComparison.Ordinal);
        var stale = lastSeen is not null && observedAt < lastSeen;
        var applied = changed && !stale;

        if (isNew)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO source_events
                    (source_feed_id, source_key, external_event_id, sport_code,
                     competition_source_entity_id, kickoff_at, event_status, context_hash,
                     attributes, first_seen_at, last_seen_at)
                VALUES
                    (@feed, @key, @external, @sport, @competition, @kickoff, @status,
                     @hash, @attributes, @observed, @observed)
                RETURNING id
                """, connection, transaction);
            AddEventParameters(insert, feed, sourceEvent, competition.Id, contextHash, observedAt);
            id = (Guid)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }
        else if (applied)
        {
            await using var update = new NpgsqlCommand(
                """
                UPDATE source_events
                SET external_event_id = @external,
                    sport_code = @sport,
                    competition_source_entity_id = @competition,
                    kickoff_at = @kickoff,
                    event_status = @status,
                    context_hash = @hash,
                    attributes = @attributes,
                    last_seen_at = @observed
                WHERE id = @id
                """, connection, transaction);
            AddEventParameters(update, feed, sourceEvent, competition.Id, contextHash, observedAt);
            update.Parameters.AddWithValue("id", id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var touch = new NpgsqlCommand(
                "UPDATE source_events SET last_seen_at = GREATEST(last_seen_at, @observed) WHERE id = @id",
                connection, transaction);
            touch.Parameters.AddWithValue("id", id);
            touch.Parameters.AddWithValue("observed", observedAt);
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }

        var participantIds = new List<Guid>();
        if (applied)
        {
            await using (var delete = new NpgsqlCommand(
                "DELETE FROM source_event_participants WHERE source_event_id = @id",
                connection, transaction))
            {
                delete.Parameters.AddWithValue("id", id);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var participant in sourceEvent.Participants)
            {
                var entityId = await ResolveEntityIdAsync(
                    connection, transaction, feed.SourceFeedId, participant.SourceEntityKey,
                    batchEntities, cancellationToken);

                if (entityId is null)
                {
                    logger.LogWarning(
                        "Event {EventKey} references unknown participant {ParticipantKey} in feed {FeedKey}",
                        sourceEvent.SourceKey,
                        participant.SourceEntityKey,
                        feed.FeedKey);
                    continue;
                }

                participantIds.Add(entityId.Value);
                await using var insert = new NpgsqlCommand(
                    """
                    INSERT INTO source_event_participants
                        (source_event_id, source_entity_id, participant_role, recorded_at)
                    VALUES (@event, @entity, @role, @observed)
                    ON CONFLICT (source_event_id, participant_role) DO UPDATE
                    SET source_entity_id = EXCLUDED.source_entity_id, recorded_at = EXCLUDED.recorded_at
                    """, connection, transaction);
                insert.Parameters.AddWithValue("event", id);
                insert.Parameters.AddWithValue("entity", entityId.Value);
                insert.Parameters.AddWithValue("role", participant.Role);
                insert.Parameters.AddWithValue("observed", observedAt);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        return new UpsertEventResult(id, isNew, applied, contextHash, participantIds);
    }

    private static void AddEventParameters(
        NpgsqlCommand command,
        FeedDescriptor feed,
        SourceEventRecord sourceEvent,
        Guid? competitionId,
        string contextHash,
        DateTimeOffset observedAt)
    {
        command.Parameters.AddWithValue("feed", feed.SourceFeedId);
        command.Parameters.AddWithValue("key", sourceEvent.SourceKey);
        command.Parameters.Add(new NpgsqlParameter("external", NpgsqlDbType.Text)
        {
            Value = (object?)sourceEvent.ExternalId ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("sport", NpgsqlDbType.Text)
        {
            Value = (object?)sourceEvent.SportCode ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("competition", NpgsqlDbType.Uuid)
        {
            Value = (object?)competitionId ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("kickoff", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)sourceEvent.KickoffAt?.ToUniversalTime() ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text)
        {
            Value = (object?)sourceEvent.Status ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("hash", contextHash);
        command.Parameters.Add(new NpgsqlParameter("attributes", NpgsqlDbType.Jsonb)
        {
            Value = sourceEvent.Attributes?.GetRawText() ?? "{}",
        });
        command.Parameters.AddWithValue("observed", observedAt);
    }

    private static async Task<CompetitionRef> ResolveCompetitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid feedId,
        string? sourceKey,
        Dictionary<string, Guid> batchEntities,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return new CompetitionRef(null, string.Empty);
        }

        if (!batchEntities.TryGetValue(sourceKey, out var id))
        {
            id = await ResolveEntityIdAsync(connection, transaction, feedId, sourceKey, batchEntities, cancellationToken)
                ?? Guid.Empty;
        }

        if (id == Guid.Empty)
        {
            return new CompetitionRef(null, NameNormalizer.Normalize(sourceKey));
        }

        await using var command = new NpgsqlCommand(
            "SELECT normalized_name FROM source_entities WHERE id = @id",
            connection, transaction);
        command.Parameters.AddWithValue("id", id);
        var normalized = await command.ExecuteScalarAsync(cancellationToken) as string;
        return new CompetitionRef(id, normalized ?? string.Empty);
    }

    private static async Task<Guid?> ResolveEntityIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid feedId,
        string sourceKey,
        Dictionary<string, Guid> batchEntities,
        CancellationToken cancellationToken)
    {
        if (batchEntities.TryGetValue(sourceKey, out var cached))
        {
            return cached;
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT id FROM source_entities
            WHERE source_feed_id = @feed AND source_key = @key
            LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("feed", feedId);
        command.Parameters.AddWithValue("key", sourceKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? id : null;
    }

    private static async Task<int> EnqueueJobAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceEntityId,
        string reason,
        string inputHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO processing_jobs
                (source_entity_id, reason, input_hash, matcher_version, status,
                 attempts, run_after, created_at)
            VALUES
                (@entity, @reason, @hash, @version, 'pending', 0, now(), now())
            ON CONFLICT ON CONSTRAINT uq_job_idempotent DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue("entity", sourceEntityId);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("hash", inputHash);
        command.Parameters.AddWithValue("version", MatchEngine.Version);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AdvanceCursorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid feedId,
        string? checkpoint,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(checkpoint))
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO sync_cursors
                (source_feed_id, stream_key, cursor_value, watermark_at, last_success_at, updated_at)
            VALUES (@feed, 'default', @cursor, now(), now(), now())
            ON CONFLICT (source_feed_id, stream_key) DO UPDATE
            SET cursor_value = EXCLUDED.cursor_value,
                watermark_at = EXCLUDED.watermark_at,
                last_success_at = EXCLUDED.last_success_at,
                updated_at = now()
            """, connection, transaction);
        command.Parameters.AddWithValue("feed", feedId);
        command.Parameters.AddWithValue("cursor", checkpoint);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record UpsertEntityResult(Guid Id, bool IsNew, bool Changed);

    private sealed record UpsertEventResult(
        Guid Id,
        bool IsNew,
        bool Changed,
        string ContextHash,
        IReadOnlyList<Guid> ParticipantEntityIds);

    private sealed record CompetitionRef(Guid? Id, string NormalizedName);
}
