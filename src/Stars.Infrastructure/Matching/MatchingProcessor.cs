using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Stars.Application.Matching;
using Stars.Application.Normalization;

namespace Stars.Infrastructure.Matching;

public sealed record MatchProcessingResult(
    bool Linked,
    Guid? MasterEntityId,
    string Reason,
    double Score,
    IReadOnlyList<ScoredCandidate> Candidates);

/// <summary>
/// Produces candidates with local indexed search, scores them with the pure engine and
/// writes links/evidence in one transaction. It never rewrites a confirmed link.
/// </summary>
public sealed class MatchingProcessor(
    NpgsqlDataSource dataSource,
    ILogger<MatchingProcessor> logger)
{
    private readonly MatchEngine _engine = new();

    public async Task<MatchProcessingResult> ProcessAsync(
        Guid sourceEntityId,
        string inputHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var profile = await LoadProfileAsync(connection, sourceEntityId, cancellationToken)
            ?? throw new InvalidOperationException($"Source entity {sourceEntityId} was not found.");

        if (!string.Equals(profile.EntityKind, "team", StringComparison.Ordinal))
        {
            return new MatchProcessingResult(false, null, "not_a_team", 0, []);
        }

        var existingMaster = await FindActiveMasterAsync(connection, sourceEntityId, cancellationToken);
        if (existingMaster is not null)
        {
            // Confirmed links are immutable evidence of a human/system decision. A new
            // observation must not silently re-point them.
            return new MatchProcessingResult(false, existingMaster, "already_linked", 0, []);
        }

        var candidates = await FindCandidatesAsync(connection, profile, cancellationToken);
        var run = _engine.Run(profile, candidates);

        if (run.Candidates.Count == 0)
        {
            await MarkReviewedAsync(connection, profile, run, inputHash, cancellationToken);
            return new MatchProcessingResult(false, null, run.Reason, 0, []);
        }

        if (!run.AutoLink || run.Top is null)
        {
            await MarkReviewedAsync(connection, profile, run, inputHash, cancellationToken);
            logger.LogInformation(
                "Entity {EntityId} ({Name}) requires review: {Reason} (score {Score:0.000}, margin {Margin:0.000})",
                sourceEntityId,
                profile.DisplayName,
                run.Reason,
                run.TopScore,
                run.Margin);

            return new MatchProcessingResult(false, null, run.Reason, run.TopScore, run.Candidates);
        }

        var masterId = await LinkAutomaticallyAsync(
            connection,
            profile,
            run,
            inputHash,
            cancellationToken);

        return new MatchProcessingResult(true, masterId, DecisionReasons.AutoLink, run.TopScore, run.Candidates);
    }

    private async Task<Guid> LinkAutomaticallyAsync(
        NpgsqlConnection connection,
        SourceEntityProfile profile,
        MatchRunResult run,
        string inputHash,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var top = run.Top!;
            var masterId = await EnsureMasterAsync(
                connection,
                transaction,
                profile,
                top.Target,
                cancellationToken);

            var decisionId = await InsertDecisionAsync(
                connection,
                transaction,
                profile.SourceEntityId,
                null,
                masterId,
                DecisionReasons.AutoLink,
                "system:matcher",
                run.Reason,
                new
                {
                    input_hash = inputHash,
                    score = run.TopScore,
                    margin = run.Margin,
                    target_master = masterId,
                    target_name = top.Target.DisplayName,
                    evidence = top.Evidence.Select(evidence => new
                    {
                        key = evidence.Key,
                        value = evidence.Value,
                        weight = evidence.Weight,
                        explanation = evidence.Explanation,
                    }),
                },
                cancellationToken);

            await EnsureLinkAsync(
                connection,
                transaction,
                profile.SourceEntityId,
                masterId,
                decisionId,
                (decimal)Math.Round(top.Score, 3),
                cancellationToken);

            await AddMasterNameAsync(
                connection,
                transaction,
                masterId,
                profile.DisplayName,
                profile.NormalizedName,
                "alias",
                cancellationToken);

            // If the candidate was a raw source entity without a canonical parent, link
            // that side too. Both links share the same canonical identity and are audited.
            if (top.Target.CandidateSourceEntityId is { } candidateSourceId)
            {
                var targetMaster = await FindActiveMasterAsync(connection, candidateSourceId, cancellationToken);
                if (targetMaster is null)
                {
                    var targetDecision = await InsertDecisionAsync(
                        connection,
                        transaction,
                        candidateSourceId,
                        null,
                        masterId,
                        DecisionReasons.AutoLink,
                        "system:matcher",
                        "linked_as_counterpart",
                        new { input_hash = inputHash, counterpart = profile.SourceEntityId, score = run.TopScore },
                        cancellationToken);
                    await EnsureLinkAsync(
                        connection,
                        transaction,
                        candidateSourceId,
                        masterId,
                        targetDecision,
                        (decimal)Math.Round(top.Score, 3),
                        cancellationToken);
                }
            }

            var candidateRows = await ReplaceCandidatesAsync(
                connection,
                transaction,
                profile,
                run,
                inputHash,
                acceptedTarget: top.Target,
                cancellationToken);

            if (candidateRows.TryGetValue(top.Target, out var acceptedCandidateId))
            {
                await using var updateDecision = new NpgsqlCommand(
                    "UPDATE match_decisions SET match_candidate_id = @candidate WHERE id = @decision",
                    connection,
                    transaction);
                updateDecision.Parameters.AddWithValue("candidate", acceptedCandidateId);
                updateDecision.Parameters.AddWithValue("decision", decisionId);
                await updateDecision.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "Linked source entity {EntityId} ({Name}) to master {MasterId} with score {Score:0.000}",
                profile.SourceEntityId,
                profile.DisplayName,
                masterId,
                run.TopScore);
            return masterId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<Guid> EnsureMasterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SourceEntityProfile profile,
        CandidateTarget target,
        CancellationToken cancellationToken)
    {
        if (target.MasterEntityId is { } masterId)
        {
            return masterId;
        }

        if (target.CandidateSourceEntityId is not { } targetSourceId)
        {
            throw new InvalidOperationException("Candidate has no target.");
        }

        var existing = await FindActiveMasterAsync(connection, targetSourceId, cancellationToken, transaction);
        if (existing is not null)
        {
            return existing.Value;
        }

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO master_entities
                (entity_kind, preferred_name, normalized_name, sport_code, country_code,
                 attributes, status, created_at, updated_at)
            VALUES
                (@kind, @name, @normalized, @sport, @country,
                 @attributes, 'active', now(), now())
            RETURNING id
            """, connection, transaction);
        insert.Parameters.AddWithValue("kind", profile.EntityKind);
        insert.Parameters.AddWithValue("name", profile.DisplayName.Trim());
        insert.Parameters.AddWithValue("normalized", profile.NormalizedName);
        insert.Parameters.Add(new NpgsqlParameter("sport", NpgsqlDbType.Text)
        {
            Value = (object?)profile.SportCode ?? DBNull.Value,
        });
        insert.Parameters.Add(new NpgsqlParameter("country", NpgsqlDbType.Text)
        {
            Value = (object?)profile.CountryCode ?? DBNull.Value,
        });
        insert.Parameters.Add(new NpgsqlParameter("attributes", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(new
            {
                created_by = "matcher",
                ruleset = MatchEngine.Version,
                first_source_entity_id = profile.SourceEntityId,
            }),
        });
        masterId = (Guid)(await insert.ExecuteScalarAsync(cancellationToken))!;

        await AddMasterNameAsync(
            connection,
            transaction,
            masterId,
            profile.DisplayName,
            profile.NormalizedName,
            "canonical",
            cancellationToken);

        if (!string.Equals(target.DisplayName, profile.DisplayName, StringComparison.Ordinal))
        {
            await AddMasterNameAsync(
                connection,
                transaction,
                masterId,
                target.DisplayName,
                target.NormalizedName,
                "alias",
                cancellationToken);
        }

        var targetDecision = await InsertDecisionAsync(
            connection,
            transaction,
            targetSourceId,
            null,
            masterId,
            "create_master",
            "system:matcher",
            "created_with_counterpart",
            new { counterpart = profile.SourceEntityId, ruleset = MatchEngine.Version },
            cancellationToken);

        await EnsureLinkAsync(
            connection,
            transaction,
            targetSourceId,
            masterId,
            targetDecision,
            null,
            cancellationToken);

        return masterId;
    }

    private async Task MarkReviewedAsync(
        NpgsqlConnection connection,
        SourceEntityProfile profile,
        MatchRunResult run,
        string inputHash,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var rows = await ReplaceCandidatesAsync(
                connection, transaction, profile, run, inputHash, acceptedTarget: null, cancellationToken);

            if (run.Top is not null
                && rows.TryGetValue(run.Top.Target, out var topCandidateId)
                && !await HasReviewDecisionAsync(connection, transaction, profile.SourceEntityId, inputHash, cancellationToken))
            {
                await InsertDecisionAsync(
                    connection,
                    transaction,
                    profile.SourceEntityId,
                    topCandidateId,
                    null,
                    "review_required",
                    "system:matcher",
                    run.Reason,
                    new
                    {
                        input_hash = inputHash,
                        score = run.TopScore,
                        margin = run.Margin,
                        candidates = run.Candidates.Select(candidate => new
                        {
                            name = candidate.Target.DisplayName,
                            source_entity_id = candidate.Target.CandidateSourceEntityId,
                            master_entity_id = candidate.Target.MasterEntityId,
                            score = candidate.Score,
                        }),
                    },
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<Dictionary<CandidateTarget, Guid>> ReplaceCandidatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SourceEntityProfile profile,
        MatchRunResult run,
        string inputHash,
        CandidateTarget? acceptedTarget,
        CancellationToken cancellationToken)
    {
        await using (var supersede = new NpgsqlCommand(
            """
            UPDATE match_candidates
            SET status = 'superseded'
            WHERE source_entity_id = @source
              AND status = 'pending'
              AND matcher_version = @version
            """, connection, transaction))
        {
            supersede.Parameters.AddWithValue("source", profile.SourceEntityId);
            supersede.Parameters.AddWithValue("version", MatchEngine.Version);
            await supersede.ExecuteNonQueryAsync(cancellationToken);
        }

        var result = new Dictionary<CandidateTarget, Guid>();
        foreach (var candidate in run.Candidates)
        {
            var status = acceptedTarget is not null && ReferenceEquals(candidate.Target, acceptedTarget)
                ? "accepted"
                : acceptedTarget is not null
                    ? "superseded"
                    : "pending";

            Guid candidateId;
            await using (var insert = new NpgsqlCommand(
                """
                INSERT INTO match_candidates
                    (source_entity_id, candidate_source_entity_id, candidate_master_entity_id,
                     score, status, matcher_version, input_hash, generated_at)
                VALUES
                    (@source, @candidate_source, @candidate_master,
                     @score, @status, @version, @hash, now())
                RETURNING id
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("source", profile.SourceEntityId);
                insert.Parameters.Add(new NpgsqlParameter("candidate_source", NpgsqlDbType.Uuid)
                {
                    Value = (object?)candidate.Target.CandidateSourceEntityId ?? DBNull.Value,
                });
                insert.Parameters.Add(new NpgsqlParameter("candidate_master", NpgsqlDbType.Uuid)
                {
                    Value = (object?)candidate.Target.MasterEntityId ?? DBNull.Value,
                });
                insert.Parameters.Add(new NpgsqlParameter("score", NpgsqlDbType.Numeric)
                {
                    Value = Math.Round((decimal)candidate.Score, 3),
                });
                insert.Parameters.AddWithValue("status", status);
                insert.Parameters.AddWithValue("version", MatchEngine.Version);
                insert.Parameters.AddWithValue("hash", inputHash);
                candidateId = (Guid)(await insert.ExecuteScalarAsync(cancellationToken))!;
            }

            result[candidate.Target] = candidateId;

            foreach (var evidence in candidate.Evidence)
            {
                await using var insertEvidence = new NpgsqlCommand(
                    """
                    INSERT INTO match_evidence
                        (match_candidate_id, evidence_key, evidence_type, weight, details, created_at)
                    VALUES (@candidate, @key, @type, @weight, @details, now())
                    ON CONFLICT (match_candidate_id, evidence_key) DO UPDATE
                    SET weight = EXCLUDED.weight, details = EXCLUDED.details
                    """, connection, transaction);
                insertEvidence.Parameters.AddWithValue("candidate", candidateId);
                insertEvidence.Parameters.AddWithValue("key", evidence.Key);
                insertEvidence.Parameters.AddWithValue("type", evidence.EvidenceType);
                insertEvidence.Parameters.Add(new NpgsqlParameter("weight", NpgsqlDbType.Numeric)
                {
                    Value = Math.Round((decimal)evidence.Weight, 4),
                });
                insertEvidence.Parameters.Add(new NpgsqlParameter("details", NpgsqlDbType.Jsonb)
                {
                    Value = JsonSerializer.Serialize(new
                    {
                        value = evidence.Value,
                        explanation = evidence.Explanation,
                        details = evidence.Details,
                    }),
                });
                await insertEvidence.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        return result;
    }

    private static async Task<Guid> InsertDecisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceEntityId,
        Guid? candidateId,
        Guid? masterEntityId,
        string decisionType,
        string decidedBy,
        string reason,
        object details,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO match_decisions
                (source_entity_id, match_candidate_id, master_entity_id, decision_type,
                 decided_by, ruleset_version, reason, decision_details, decided_at)
            VALUES
                (@source, @candidate, @master, @type, @by, @version, @reason, @details, now())
            RETURNING id
            """, connection, transaction);
        command.Parameters.AddWithValue("source", sourceEntityId);
        command.Parameters.Add(new NpgsqlParameter("candidate", NpgsqlDbType.Uuid)
        {
            Value = (object?)candidateId ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("master", NpgsqlDbType.Uuid)
        {
            Value = (object?)masterEntityId ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("type", decisionType);
        command.Parameters.AddWithValue("by", decidedBy);
        command.Parameters.AddWithValue("version", MatchEngine.Version);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.Add(new NpgsqlParameter("details", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(details),
        });
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task EnsureLinkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceEntityId,
        Guid masterEntityId,
        Guid decisionId,
        decimal? confidence,
        CancellationToken cancellationToken)
    {
        var existing = await FindActiveMasterAsync(connection, sourceEntityId, cancellationToken, transaction);
        if (existing is not null)
        {
            if (existing.Value == masterEntityId)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Source entity {sourceEntityId} already has active master {existing}; refusing to rewrite a confirmed link.");
        }

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO entity_links
                (source_entity_id, master_entity_id, decision_id, confidence, valid_from)
            VALUES (@source, @master, @decision, @confidence, now())
            """, connection, transaction);
        command.Parameters.AddWithValue("source", sourceEntityId);
        command.Parameters.AddWithValue("master", masterEntityId);
        command.Parameters.AddWithValue("decision", decisionId);
        command.Parameters.Add(new NpgsqlParameter("confidence", NpgsqlDbType.Numeric)
        {
            Value = (object?)confidence ?? DBNull.Value,
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AddMasterNameAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid masterId,
        string name,
        string normalizedName,
        string kind,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO master_names
                (master_entity_id, name, normalized_name, name_kind, recorded_at)
            SELECT @master, @name, @normalized, @kind, now()
            WHERE NOT EXISTS (
                SELECT 1 FROM master_names
                WHERE master_entity_id = @master AND name = @name)
            """, connection, transaction);
        command.Parameters.AddWithValue("master", masterId);
        command.Parameters.AddWithValue("name", name.Trim());
        command.Parameters.AddWithValue("normalized", normalizedName);
        command.Parameters.AddWithValue("kind", kind);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasReviewDecisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceEntityId,
        string inputHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1 FROM match_decisions
                WHERE source_entity_id = @source
                  AND decision_type = 'review_required'
                  AND decision_details ->> 'input_hash' = @hash
            )
            """, connection, transaction);
        command.Parameters.AddWithValue("source", sourceEntityId);
        command.Parameters.AddWithValue("hash", inputHash);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<Guid?> FindActiveMasterAsync(
        NpgsqlConnection connection,
        Guid sourceEntityId,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT master_entity_id FROM entity_links
            WHERE source_entity_id = @source AND valid_to IS NULL
            LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("source", sourceEntityId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? id : null;
    }

    private async Task<SourceEntityProfile?> LoadProfileAsync(
        NpgsqlConnection connection,
        Guid sourceEntityId,
        CancellationToken cancellationToken)
    {
        SourceEntityProfile? profile = null;
        await using (var command = new NpgsqlCommand(
            """
            SELECT se.id, se.source_feed_id, sf.feed_key, se.entity_kind, se.display_name,
                   se.normalized_name, se.sport_code, se.country_code
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
                    []);
            }
        }

        if (profile is null)
        {
            return null;
        }

        var contexts = await LoadContextsAsync(connection, [sourceEntityId], cancellationToken);
        return profile with { Context = contexts.GetValueOrDefault(sourceEntityId, []) };
    }

    private async Task<IReadOnlyList<CandidateTarget>> FindCandidatesAsync(
        NpgsqlConnection connection,
        SourceEntityProfile profile,
        CancellationToken cancellationToken)
    {
        var rawSources = new List<SourceCandidateRow>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT se.id, se.display_name, se.normalized_name, se.sport_code, se.country_code,
                   sf.feed_key, similarity(se.normalized_name, @name) AS sim,
                   el.master_entity_id,
                   me.preferred_name, me.normalized_name, me.sport_code, me.country_code
            FROM source_entities se
            JOIN source_feeds sf ON sf.id = se.source_feed_id
            LEFT JOIN entity_links el
                ON el.source_entity_id = se.id AND el.valid_to IS NULL
            LEFT JOIN master_entities me ON me.id = el.master_entity_id
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
                    reader.GetDouble(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11)));
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
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetDouble(5)));
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
            SELECT p.source_entity_id, opponent.normalized_name, e.kickoff_at,
                   competition.normalized_name, p.participant_role
            FROM source_event_participants p
            JOIN source_events e ON e.id = p.source_event_id
            JOIN source_event_participants op
                ON op.source_event_id = e.id AND op.source_entity_id <> p.source_entity_id
            JOIN source_entities opponent ON opponent.id = op.source_entity_id
            LEFT JOIN source_entities competition ON competition.id = e.competition_source_entity_id
            WHERE p.source_entity_id = ANY(@ids)
            ORDER BY e.kickoff_at DESC NULLS LAST
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

            if (facts.Count >= 12)
            {
                continue;
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
        double Similarity,
        Guid? MasterEntityId,
        string? MasterPreferredName,
        string? MasterNormalizedName,
        string? MasterSportCode,
        string? MasterCountryCode);

    private sealed record MasterCandidateRow(
        Guid Id,
        string PreferredName,
        string NormalizedName,
        string? SportCode,
        string? CountryCode,
        double Similarity);
}
