using Npgsql;
using Stars.Application.Matching;

namespace Stars.Infrastructure.Matching;

internal static class CandidateSearch
{
    public static async Task<SourceEntityProfile?> LoadProfileAsync(
        NpgsqlConnection connection,
        Guid sourceEntityId,
        CancellationToken cancellationToken)
    {
        SourceEntityProfile? profile = null;
        await using (var command = new NpgsqlCommand(
            """
            SELECT se.id, se.source_feed_id, sf.feed_key, se.entity_kind, se.display_name,
                   se.normalized_name, se.sport_code, se.country_code,
                   (SELECT count(*) FROM source_entities same
                    WHERE same.source_feed_id = se.source_feed_id
                      AND same.entity_kind = se.entity_kind
                      AND same.normalized_name = se.normalized_name)
            FROM source_entities se
            JOIN source_feeds sf ON sf.id = se.source_feed_id
            WHERE se.id = @id
            """, connection))
        {
            command.Parameters.AddWithValue("id", sourceEntityId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                profile = new SourceEntityProfile(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    [],
                    (int)reader.GetInt64(8));
            }
        }

        if (profile is null)
        {
            return null;
        }

        var contexts = await LoadContextsAsync(connection, [sourceEntityId], cancellationToken);
        return profile with { Context = contexts.GetValueOrDefault(sourceEntityId, []) };
    }

    public static async Task<IReadOnlyList<CandidateTarget>> FindCandidatesAsync(
        NpgsqlConnection connection,
        SourceEntityProfile profile,
        CancellationToken cancellationToken)
    {
        var rawSources = new List<SourceCandidateRow>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT se.id, se.display_name, se.normalized_name, se.sport_code, se.country_code,
                   sf.feed_key, similarity(se.normalized_name, @name) AS sim,
                   el.master_entity_id
            FROM source_entities se
            JOIN source_feeds sf ON sf.id = se.source_feed_id
            LEFT JOIN entity_links el
                ON el.source_entity_id = se.id AND el.valid_to IS NULL
            WHERE se.entity_kind = @kind
              AND se.source_feed_id <> @feed
              AND (se.normalized_name = @name OR se.normalized_name % @name)
            ORDER BY sim DESC, se.last_seen_at DESC
            LIMIT 24
            """, connection))
        {
            command.Parameters.AddWithValue("kind", profile.EntityKind);
            command.Parameters.AddWithValue("feed", profile.SourceFeedId);
            command.Parameters.AddWithValue("name", profile.NormalizedName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rawSources.Add(new SourceCandidateRow(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7)));
            }
        }

        var masters = new List<MasterCandidateRow>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT me.id, me.preferred_name, me.normalized_name, me.sport_code, me.country_code,
                   similarity(me.normalized_name, @name) AS sim
            FROM master_entities me
            WHERE me.entity_kind = @kind
              AND EXISTS (
                  SELECT 1 FROM entity_links el
                  WHERE el.master_entity_id = me.id AND el.valid_to IS NULL)
              AND (me.normalized_name = @name OR me.normalized_name % @name)
            ORDER BY sim DESC
            LIMIT 24
            """, connection))
        {
            command.Parameters.AddWithValue("kind", profile.EntityKind);
            command.Parameters.AddWithValue("name", profile.NormalizedName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                masters.Add(new MasterCandidateRow(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        var sourceIdsForContext = rawSources.Select(row => row.SourceEntityId).ToList();
        var masterIdsForContext = masters.Select(row => row.Id).ToList();
        var linkedSourcesByMaster = new Dictionary<Guid, List<Guid>>();
        if (masterIdsForContext.Count > 0)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT master_entity_id, source_entity_id
                FROM entity_links
                WHERE master_entity_id = ANY(@masters) AND valid_to IS NULL
                """, connection);
            command.Parameters.AddWithValue("masters", masterIdsForContext.ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var masterId = reader.GetGuid(0);
                if (!linkedSourcesByMaster.TryGetValue(masterId, out var ids))
                {
                    linkedSourcesByMaster[masterId] = ids = [];
                }

                ids.Add(reader.GetGuid(1));
            }

            foreach (var ids in linkedSourcesByMaster.Values)
            {
                sourceIdsForContext.AddRange(ids);
            }
        }

        var contexts = await LoadContextsAsync(
            connection,
            sourceIdsForContext.Distinct().ToArray(),
            cancellationToken);

        var targets = new Dictionary<string, CandidateTarget>(StringComparer.Ordinal);
        foreach (var row in rawSources)
        {
            var context = new List<MatchContextFact>();
            if (contexts.TryGetValue(row.SourceEntityId, out var sourceContext))
            {
                context.AddRange(sourceContext);
            }

            if (row.MasterEntityId is { } masterId)
            {
                if (linkedSourcesByMaster.TryGetValue(masterId, out var linkedIds))
                {
                    foreach (var linkedId in linkedIds)
                    {
                        if (contexts.TryGetValue(linkedId, out var linkedContext))
                        {
                            context.AddRange(linkedContext);
                        }
                    }
                }

                var masterRow = masters.FirstOrDefault(item => item.Id == masterId);
                var target = new CandidateTarget(
                    null,
                    masterId,
                    row.FeedKey,
                    masterRow?.PreferredName ?? row.DisplayName,
                    masterRow?.NormalizedName ?? row.NormalizedName,
                    masterRow?.SportCode ?? row.SportCode,
                    masterRow?.CountryCode ?? row.CountryCode,
                    context);
                targets[$"m:{masterId}"] = target;
            }
            else
            {
                var target = new CandidateTarget(
                    row.SourceEntityId,
                    null,
                    row.FeedKey,
                    row.DisplayName,
                    row.NormalizedName,
                    row.SportCode,
                    row.CountryCode,
                    context);
                targets[$"s:{row.SourceEntityId}"] = target;
            }
        }

        foreach (var master in masters)
        {
            if (targets.ContainsKey($"m:{master.Id}"))
            {
                continue;
            }

            var context = new List<MatchContextFact>();
            if (linkedSourcesByMaster.TryGetValue(master.Id, out var linkedIds))
            {
                foreach (var linkedId in linkedIds)
                {
                    if (contexts.TryGetValue(linkedId, out var linkedContext))
                    {
                        context.AddRange(linkedContext);
                    }
                }
            }

            targets[$"m:{master.Id}"] = new CandidateTarget(
                null,
                master.Id,
                "master",
                master.PreferredName,
                master.NormalizedName,
                master.SportCode,
                master.CountryCode,
                context);
        }

        return targets.Values.ToList();
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<MatchContextFact>>> LoadContextsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<Guid> sourceEntityIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<MatchContextFact>>();
        if (sourceEntityIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<MatchContextFact>>();
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT ids.id, context.normalized_name, context.kickoff_at,
                   context.competition_name, context.participant_role
            FROM unnest(@ids::uuid[]) AS ids(id)
            CROSS JOIN LATERAL (
                SELECT opponent.normalized_name, e.kickoff_at,
                       competition.normalized_name AS competition_name, p.participant_role
                FROM source_event_participants p
                JOIN source_events e ON e.id = p.source_event_id
                JOIN source_event_participants op
                    ON op.source_event_id = e.id AND op.source_entity_id <> p.source_entity_id
                JOIN source_entities opponent ON opponent.id = op.source_entity_id
                LEFT JOIN source_entities competition ON competition.id = e.competition_source_entity_id
                WHERE p.source_entity_id = ids.id
                ORDER BY e.kickoff_at DESC NULLS LAST
                LIMIT 12
            ) context
            ORDER BY context.kickoff_at DESC NULLS LAST
            """, connection);
        command.Parameters.AddWithValue("ids", sourceEntityIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var entityId = reader.GetGuid(0);
            if (!result.TryGetValue(entityId, out var facts))
            {
                result[entityId] = facts = [];
            }

            facts.Add(new MatchContextFact(
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<MatchContextFact>)pair.Value);
    }

    private sealed record SourceCandidateRow(
        Guid SourceEntityId,
        string DisplayName,
        string NormalizedName,
        string? SportCode,
        string? CountryCode,
        string FeedKey,
        Guid? MasterEntityId);

    private sealed record MasterCandidateRow(
        Guid Id,
        string PreferredName,
        string NormalizedName,
        string? SportCode,
        string? CountryCode);
}
