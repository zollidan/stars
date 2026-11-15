using Npgsql;
using Stars.Application.Registry;

namespace Stars.Infrastructure.Registry;

public sealed class RegistryQueryService(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<SourceFeedView>> ListSourcesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT pc.id, pc.name, pc.connector_key,
                   sf.id, sf.feed_key, sf.entity_kind, sf.dataset_ref, sf.sync_mode, sf.is_enabled,
                   sc.cursor_value, sc.last_success_at,
                   sr.status, sr.started_at, sr.error_message,
                   (SELECT count(*) FROM source_entities se WHERE se.source_feed_id = sf.id),
                   (SELECT count(*) FROM source_events ev WHERE ev.source_feed_id = sf.id),
                   EXISTS (
                       SELECT 1
                       FROM processing_jobs job
                       JOIN source_entities entity ON entity.id = job.source_entity_id
                       WHERE entity.source_feed_id = sf.id
                         AND job.status IN ('pending', 'processing'))
            FROM source_feeds sf
            JOIN provider_connections pc ON pc.id = sf.provider_connection_id
            LEFT JOIN sync_cursors sc
                ON sc.source_feed_id = sf.id AND sc.stream_key = 'default'
            LEFT JOIN LATERAL (
                SELECT status, started_at, error_message
                FROM sync_runs
                WHERE source_feed_id = sf.id
                ORDER BY started_at DESC
                LIMIT 1
            ) sr ON true
            ORDER BY pc.name, sf.feed_key
            """, connection);

        var result = new List<SourceFeedView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SourceFeedView(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.GetBoolean(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                reader.GetInt64(14),
                reader.GetInt64(15),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.GetBoolean(16)));
        }

        return result;
    }

    public async Task<IReadOnlyList<DirectoryEntryView>> ListDirectoryAsync(
        Guid? masterEntityId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var masters = new List<DirectoryEntryView>();
        var sourcesByMaster = new Dictionary<Guid, List<SourceLinkView>>();
        var sourceIds = new List<Guid>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT me.id, me.entity_kind, me.preferred_name, me.normalized_name,
                   me.sport_code, me.country_code, me.status, me.created_at,
                   link.source_entity_id, se.source_feed_id, sf.feed_key, pc.name,
                   se.display_name, se.external_id, se.first_seen_at, se.last_seen_at
            FROM master_entities me
            JOIN entity_links link
                ON link.master_entity_id = me.id AND link.valid_to IS NULL
            JOIN source_entities se ON se.id = link.source_entity_id
            JOIN source_feeds sf ON sf.id = se.source_feed_id
            JOIN provider_connections pc ON pc.id = sf.provider_connection_id
            WHERE me.status = 'active'
              AND (@master IS NULL OR me.id = @master)
            ORDER BY me.preferred_name, pc.name
            """, connection))
        {
            command.Parameters.Add(new NpgsqlParameter("master", NpgsqlTypes.NpgsqlDbType.Uuid)
            {
                Value = (object?)masterEntityId ?? DBNull.Value,
            });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var seenMasters = new HashSet<Guid>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var masterId = reader.GetGuid(0);
                if (seenMasters.Add(masterId))
                {
                    masters.Add(new DirectoryEntryView(
                        masterId,
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.GetString(6),
                        reader.GetFieldValue<DateTimeOffset>(7),
                        [],
                        []));
                }

                var sourceId = reader.GetGuid(8);
                sourceIds.Add(sourceId);
                if (!sourcesByMaster.TryGetValue(masterId, out var links))
                {
                    sourcesByMaster[masterId] = links = [];
                }

                links.Add(new SourceLinkView(
                    sourceId,
                    reader.GetGuid(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.GetFieldValue<DateTimeOffset>(14),
                    reader.GetFieldValue<DateTimeOffset>(15),
                    []));
            }
        }

        var masterNames = await LoadMasterNamesAsync(connection, masters.Select(master => master.MasterEntityId).ToArray(), cancellationToken);
        var sourceNames = await LoadSourceNamesAsync(connection, sourceIds.Distinct().ToArray(), cancellationToken);

        return masters.Select(master =>
        {
            var links = sourcesByMaster.GetValueOrDefault(master.MasterEntityId, [])
                .Select(link => link with { Names = sourceNames.GetValueOrDefault(link.SourceEntityId, []) })
                .ToList();

            return master with
            {
                Names = masterNames.GetValueOrDefault(master.MasterEntityId, []),
                Sources = links,
            };
        }).ToList();
    }

    public async Task<MetricsView> GetMetricsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*) FROM source_entities),
                (SELECT count(*) FROM source_events),
                (SELECT count(*) FROM provider_connections),
                (SELECT count(*) FROM source_feeds),
                (SELECT count(*) FROM master_entities WHERE status = 'active'),
                (SELECT count(*) FROM entity_links WHERE valid_to IS NULL),
                (SELECT count(*) FROM match_candidates WHERE status = 'pending'),
                (SELECT count(*) FROM processing_jobs WHERE status = 'pending'),
                (SELECT count(*) FROM processing_jobs WHERE status = 'processing'),
                (SELECT count(*) FROM processing_jobs WHERE status = 'done'),
                (SELECT count(*) FROM sync_runs WHERE status = 'failed'),
                (SELECT coalesce(sum(records_read), 0) FROM sync_runs),
                (SELECT coalesce(avg(extract(epoch FROM (completed_at - started_at))), 0)
                 FROM sync_runs WHERE completed_at IS NOT NULL)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new MetricsView(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetDouble(12));
    }

    public async Task<DirectoryEntryView?> GetMasterAsync(Guid masterEntityId, CancellationToken cancellationToken)
    {
        var entries = await ListDirectoryAsync(masterEntityId, cancellationToken);
        return entries.SingleOrDefault();
    }

    public async Task<IReadOnlyList<ReviewItemView>> ListReviewsAsync(
        Guid? masterEntityId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var items = new List<ReviewItemView>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT mc.id, mc.source_entity_id, se.display_name, sf.feed_key, pc.name,
                   COALESCE(me.preferred_name, target.display_name, 'unknown target'),
                   mc.candidate_master_entity_id, mc.candidate_source_entity_id,
                   mc.score, mc.status, mc.generated_at, mc.input_hash
            FROM match_candidates mc
            JOIN source_entities se ON se.id = mc.source_entity_id
            JOIN source_feeds sf ON sf.id = se.source_feed_id
            JOIN provider_connections pc ON pc.id = sf.provider_connection_id
            LEFT JOIN master_entities me ON me.id = mc.candidate_master_entity_id
            LEFT JOIN source_entities target ON target.id = mc.candidate_source_entity_id
            WHERE mc.status = 'pending'
              AND (@master IS NULL OR mc.candidate_master_entity_id = @master)
            ORDER BY mc.generated_at DESC, mc.score DESC
            LIMIT 250
            """, connection))
        {
            command.Parameters.Add(new NpgsqlParameter("master", NpgsqlTypes.NpgsqlDbType.Uuid)
            {
                Value = (object?)masterEntityId ?? DBNull.Value,
            });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new ReviewItemView(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetGuid(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7),
                    reader.IsDBNull(8) ? null : (double)reader.GetDecimal(8),
                    reader.GetString(9),
                    reader.GetFieldValue<DateTimeOffset>(10),
                    reader.GetString(11),
                    []));
            }
        }

        var evidence = await LoadEvidenceAsync(
            connection,
            items.Select(item => item.CandidateId).ToArray(),
            cancellationToken);

        return items
            .Select(item => item with { Evidence = evidence.GetValueOrDefault(item.CandidateId, []) })
            .ToList();
    }

    public async Task<ResolveView?> ResolveAsync(
        Guid sourceFeedId,
        string entityKind,
        string sourceKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT se.id, se.source_feed_id, sf.feed_key, se.entity_kind, se.source_key,
                   se.display_name, me.id, me.preferred_name, link.valid_from, link.confidence
            FROM source_entities se
            JOIN source_feeds sf ON sf.id = se.source_feed_id
            JOIN entity_links link
                ON link.source_entity_id = se.id AND link.valid_to IS NULL
            JOIN master_entities me ON me.id = link.master_entity_id
            WHERE se.source_feed_id = @feed
              AND se.entity_kind = @kind
              AND se.source_key = @key
            LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("feed", sourceFeedId);
        command.Parameters.AddWithValue("kind", entityKind);
        command.Parameters.AddWithValue("key", sourceKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ResolveView(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetGuid(6),
            reader.GetString(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetDecimal(9));
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<string>>> LoadMasterNamesAsync(
        NpgsqlConnection connection,
        Guid[] masterIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<string>>();
        if (masterIds.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<string>>();
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT master_entity_id, name
            FROM master_names
            WHERE master_entity_id = ANY(@ids)
            ORDER BY (name_kind = 'canonical') DESC, name
            """, connection);
        command.Parameters.AddWithValue("ids", masterIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            if (!result.TryGetValue(id, out var names))
            {
                result[id] = names = [];
            }

            names.Add(reader.GetString(1));
        }

        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value);
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<string>>> LoadSourceNamesAsync(
        NpgsqlConnection connection,
        Guid[] sourceIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<string>>();
        if (sourceIds.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<string>>();
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT source_entity_id, name
            FROM source_entity_names
            WHERE source_entity_id = ANY(@ids)
            ORDER BY is_current DESC, last_seen_at DESC
            """, connection);
        command.Parameters.AddWithValue("ids", sourceIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            if (!result.TryGetValue(id, out var names))
            {
                result[id] = names = [];
            }

            names.Add(reader.GetString(1));
        }

        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value);
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<EvidenceView>>> LoadEvidenceAsync(
        NpgsqlConnection connection,
        Guid[] candidateIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<EvidenceView>>();
        if (candidateIds.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<EvidenceView>>();
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT match_candidate_id, evidence_key, evidence_type, weight,
                   COALESCE(details ->> 'explanation', ''),
                   COALESCE(details ->> 'details', '{}')
            FROM match_evidence
            WHERE match_candidate_id = ANY(@ids)
            ORDER BY evidence_key
            """, connection);
        command.Parameters.AddWithValue("ids", candidateIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            if (!result.TryGetValue(id, out var evidence))
            {
                result[id] = evidence = [];
            }

            evidence.Add(new EvidenceView(
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : (double)reader.GetDecimal(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<EvidenceView>)pair.Value);
    }
}
