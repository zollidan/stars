using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Stars.Application.Matching;
using static Stars.Infrastructure.Matching.CandidateSearch;
using static Stars.Infrastructure.Registry.RegistryWrites;

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

        masterId = await InsertMasterAsync(connection, transaction, profile.EntityKind,
            profile.DisplayName, profile.NormalizedName, profile.SportCode, profile.CountryCode,
            new
            {
                created_by = "matcher",
                ruleset = MatchEngine.Version,
                first_source_entity_id = profile.SourceEntityId,
            }, cancellationToken);

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

        await InsertLinkAsync(connection, transaction, sourceEntityId, masterEntityId,
            decisionId, confidence, cancellationToken);
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
}
