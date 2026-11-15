using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Stars.Application.Matching;
using Stars.Application.Normalization;

namespace Stars.Infrastructure.Registry;

public sealed class LinkConflictException(string message) : Exception(message);

/// <summary>
/// Human decisions. Confirmed links are closed, never rewritten: a conflict is surfaced
/// for review instead of silently re-pointing a source entity.
/// </summary>
public sealed class ReviewService(NpgsqlDataSource dataSource)
{
    public async Task<Guid> ConfirmAsync(Guid candidateId, string reviewer, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var candidate = await LoadCandidateAsync(connection, transaction, candidateId, cancellationToken)
                ?? throw new KeyNotFoundException($"Candidate {candidateId} was not found.");

            if (candidate.Status == "accepted")
            {
                return await FindActiveMasterAsync(connection, transaction, candidate.SourceEntityId, cancellationToken)
                    ?? throw new InvalidOperationException("Candidate is accepted but no active link exists.");
            }

            if (candidate.Status != "pending")
            {
                throw new InvalidOperationException($"Candidate is '{candidate.Status}' and cannot be confirmed.");
            }

            var existingMaster = await FindActiveMasterAsync(
                connection, transaction, candidate.SourceEntityId, cancellationToken);

            var masterId = candidate.CandidateMasterEntityId
                ?? await ResolveOrCreateMasterAsync(connection, transaction, candidate, reviewer, cancellationToken);

            if (existingMaster is not null && existingMaster != masterId)
            {
                throw new LinkConflictException(
                    $"Source entity already has active master {existingMaster}; close that link explicitly before linking to {masterId}.");
            }

            if (existingMaster is null)
            {
                var decisionId = await InsertDecisionAsync(
                    connection,
                    transaction,
                    candidate.SourceEntityId,
                    candidateId,
                    masterId,
                    "manual_link",
                    reviewer,
                    "review_confirmed",
                    new { score = candidate.Score, candidate_id = candidateId },
                    cancellationToken);

                await InsertLinkAsync(
                    connection, transaction, candidate.SourceEntityId, masterId, decisionId,
                    candidate.Score is null ? null : Math.Round(candidate.Score.Value, 3), cancellationToken);
            }

            if (candidate.CandidateSourceEntityId is { } targetSourceId)
            {
                var targetMaster = await FindActiveMasterAsync(connection, transaction, targetSourceId, cancellationToken);
                if (targetMaster is null)
                {
                    var targetDecision = await InsertDecisionAsync(
                        connection,
                        transaction,
                        targetSourceId,
                        null,
                        masterId,
                        "manual_link",
                        reviewer,
                        "linked_as_counterpart",
                        new { counterpart = candidate.SourceEntityId, candidate_id = candidateId },
                        cancellationToken);
                    await InsertLinkAsync(
                        connection, transaction, targetSourceId, masterId, targetDecision,
                        candidate.Score is null ? null : Math.Round(candidate.Score.Value, 3), cancellationToken);
                }
                else if (targetMaster != masterId)
                {
                    throw new LinkConflictException(
                        $"Counterpart source entity is already linked to a different master {targetMaster}.");
                }
            }

            await AddMasterNameAsync(connection, transaction, masterId, candidate.SourceName, cancellationToken);
            if (candidate.CandidateSourceName is not null)
            {
                await AddMasterNameAsync(connection, transaction, masterId, candidate.CandidateSourceName, cancellationToken);
            }

            await using (var accept = new NpgsqlCommand(
                "UPDATE match_candidates SET status = 'accepted' WHERE id = @id",
                connection, transaction))
            {
                accept.Parameters.AddWithValue("id", candidateId);
                await accept.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var supersede = new NpgsqlCommand(
                """
                UPDATE match_candidates
                SET status = 'superseded'
                WHERE source_entity_id = @source AND status = 'pending' AND id <> @id
                """, connection, transaction))
            {
                supersede.Parameters.AddWithValue("source", candidate.SourceEntityId);
                supersede.Parameters.AddWithValue("id", candidateId);
                await supersede.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return masterId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task RejectAsync(Guid candidateId, string reviewer, string? reason, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var candidate = await LoadCandidateAsync(connection, transaction, candidateId, cancellationToken)
                ?? throw new KeyNotFoundException($"Candidate {candidateId} was not found.");

            if (candidate.Status != "pending")
            {
                throw new InvalidOperationException($"Candidate is '{candidate.Status}' and cannot be rejected.");
            }

            await InsertDecisionAsync(
                connection,
                transaction,
                candidate.SourceEntityId,
                candidateId,
                null,
                "reject",
                reviewer,
                reason ?? "review_rejected",
                new { candidate_id = candidateId, target = candidate.CandidateSourceName },
                cancellationToken);

            await using var update = new NpgsqlCommand(
                "UPDATE match_candidates SET status = 'rejected' WHERE id = @id",
                connection, transaction);
            update.Parameters.AddWithValue("id", candidateId);
            await update.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task UnlinkAsync(
        Guid masterEntityId,
        Guid sourceEntityId,
        string reviewer,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var find = new NpgsqlCommand(
                """
                SELECT id FROM entity_links
                WHERE source_entity_id = @source AND master_entity_id = @master AND valid_to IS NULL
                FOR UPDATE
                """, connection, transaction);
            find.Parameters.AddWithValue("source", sourceEntityId);
            find.Parameters.AddWithValue("master", masterEntityId);
            var linkId = await find.ExecuteScalarAsync(cancellationToken);
            if (linkId is not Guid id)
            {
                throw new KeyNotFoundException("Active link was not found.");
            }

            var decisionId = await InsertDecisionAsync(
                connection,
                transaction,
                sourceEntityId,
                null,
                masterEntityId,
                "unlink",
                reviewer,
                reason ?? "link_closed",
                new { link_id = id },
                cancellationToken);

            await using var close = new NpgsqlCommand(
                """
                UPDATE entity_links
                SET valid_to = now()
                WHERE id = @id
                """, connection, transaction);
            close.Parameters.AddWithValue("id", id);
            await close.ExecuteNonQueryAsync(cancellationToken);

            // Keep the decision id in the audit trail even though the link is closed.
            await using var annotate = new NpgsqlCommand(
                "UPDATE match_decisions SET decision_details = decision_details || @details::jsonb WHERE id = @id",
                connection, transaction);
            annotate.Parameters.AddWithValue("id", decisionId);
            annotate.Parameters.Add(new NpgsqlParameter("details", NpgsqlDbType.Jsonb)
            {
                Value = JsonSerializer.Serialize(new { link_id = id }),
            });
            await annotate.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<Guid> ResolveOrCreateMasterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CandidateRow candidate,
        string reviewer,
        CancellationToken cancellationToken)
    {
        if (candidate.CandidateSourceEntityId is not { } targetSourceId)
        {
            throw new InvalidOperationException("Candidate has no target.");
        }

        var existing = await FindActiveMasterAsync(connection, transaction, targetSourceId, cancellationToken);
        if (existing is not null)
        {
            return existing.Value;
        }

        var target = await LoadSourceEntityAsync(connection, transaction, targetSourceId, cancellationToken)
            ?? throw new InvalidOperationException("Candidate target source entity was not found.");

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO master_entities
                (entity_kind, preferred_name, normalized_name, sport_code, country_code,
                 attributes, status, created_at, updated_at)
            VALUES
                (@kind, @name, @normalized, @sport, @country, @attributes, 'active', now(), now())
            RETURNING id
            """, connection, transaction);
        insert.Parameters.AddWithValue("kind", target.EntityKind);
        insert.Parameters.AddWithValue("name", target.DisplayName.Trim());
        insert.Parameters.AddWithValue("normalized", target.NormalizedName);
        insert.Parameters.Add(new NpgsqlParameter("sport", NpgsqlDbType.Text)
        {
            Value = (object?)target.SportCode ?? DBNull.Value,
        });
        insert.Parameters.Add(new NpgsqlParameter("country", NpgsqlDbType.Text)
        {
            Value = (object?)target.CountryCode ?? DBNull.Value,
        });
        insert.Parameters.Add(new NpgsqlParameter("attributes", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(new
            {
                created_by = reviewer,
                ruleset = MatchEngine.Version,
                first_source_entity_id = targetSourceId,
            }),
        });
        var masterId = (Guid)(await insert.ExecuteScalarAsync(cancellationToken))!;

        await AddMasterNameAsync(connection, transaction, masterId, target.DisplayName, cancellationToken);

        var decisionId = await InsertDecisionAsync(
            connection,
            transaction,
            targetSourceId,
            null,
            masterId,
            "create_master",
            reviewer,
            "created_from_review",
            new { candidate_id = candidate.Id },
            cancellationToken);
        await InsertLinkAsync(connection, transaction, targetSourceId, masterId, decisionId, null, cancellationToken);

        return masterId;
    }

    private static async Task<CandidateRow?> LoadCandidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT mc.id, mc.source_entity_id, mc.candidate_source_entity_id,
                   mc.candidate_master_entity_id, mc.status, mc.score,
                   se.display_name, target.display_name
            FROM match_candidates mc
            JOIN source_entities se ON se.id = mc.source_entity_id
            LEFT JOIN source_entities target ON target.id = mc.candidate_source_entity_id
            WHERE mc.id = @id
            FOR UPDATE OF mc
            """, connection, transaction);
        command.Parameters.AddWithValue("id", candidateId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CandidateRow(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : (double)reader.GetDecimal(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    private static async Task<SourceEntityRow?> LoadSourceEntityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceEntityId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT entity_kind, display_name, normalized_name, sport_code, country_code
            FROM source_entities WHERE id = @id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", sourceEntityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SourceEntityRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private static async Task<Guid?> FindActiveMasterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceEntityId,
        CancellationToken cancellationToken)
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

    private static async Task InsertLinkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceEntityId,
        Guid masterEntityId,
        Guid decisionId,
        double? confidence,
        CancellationToken cancellationToken)
    {
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
            Value = confidence is null ? DBNull.Value : Math.Round((decimal)confidence.Value, 3),
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AddMasterNameAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid masterId,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO master_names
                (master_entity_id, name, normalized_name, name_kind, recorded_at)
            SELECT @master, @name, @normalized, 'alias', now()
            WHERE NOT EXISTS (
                SELECT 1 FROM master_names
                WHERE master_entity_id = @master AND name = @name)
            """, connection, transaction);
        command.Parameters.AddWithValue("master", masterId);
        command.Parameters.AddWithValue("name", name.Trim());
        command.Parameters.AddWithValue("normalized", NameNormalizer.Normalize(name));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record CandidateRow(
        Guid Id,
        Guid SourceEntityId,
        Guid? CandidateSourceEntityId,
        Guid? CandidateMasterEntityId,
        string Status,
        double? Score,
        string SourceName,
        string? CandidateSourceName);

    private sealed record SourceEntityRow(
        string EntityKind,
        string DisplayName,
        string NormalizedName,
        string? SportCode,
        string? CountryCode);
}
