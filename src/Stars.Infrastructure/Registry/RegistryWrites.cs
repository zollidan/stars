using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Stars.Application.Matching;

namespace Stars.Infrastructure.Registry;

internal static class RegistryWrites
{
    public static async Task<Guid> InsertMasterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string entityKind,
        string name,
        string normalizedName,
        string? sportCode,
        string? countryCode,
        object attributes,
        CancellationToken cancellationToken)
    {
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
        insert.Parameters.AddWithValue("kind", entityKind);
        insert.Parameters.AddWithValue("name", name.Trim());
        insert.Parameters.AddWithValue("normalized", normalizedName);
        insert.Parameters.Add(new NpgsqlParameter("sport", NpgsqlDbType.Text)
        {
            Value = (object?)sportCode ?? DBNull.Value,
        });
        insert.Parameters.Add(new NpgsqlParameter("country", NpgsqlDbType.Text)
        {
            Value = (object?)countryCode ?? DBNull.Value,
        });
        insert.Parameters.Add(new NpgsqlParameter("attributes", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(attributes),
        });
        return (Guid)(await insert.ExecuteScalarAsync(cancellationToken))!;
    }

    public static async Task<Guid> InsertDecisionAsync(
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

    public static async Task InsertLinkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceEntityId,
        Guid masterEntityId,
        Guid decisionId,
        decimal? confidence,
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
            Value = (object?)confidence ?? DBNull.Value,
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task AddMasterNameAsync(
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

    public static async Task<Guid?> FindActiveMasterAsync(
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
}
