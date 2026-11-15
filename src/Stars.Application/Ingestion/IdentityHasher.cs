using System.Security.Cryptography;
using System.Text;
using Stars.Application.Normalization;
using Stars.Connectors.Abstractions;

namespace Stars.Application.Ingestion;

/// <summary>
/// Stable content hashes used for identity and idempotency. Hashing is deliberately
/// based on stable observed fields only: a repeated odds snapshot or a richer raw payload
/// must not look like an entity change.
/// </summary>
public static class IdentityHasher
{
    public static string Entity(SourceEntityRecord record)
    {
        var normalizedName = NameNormalizer.Normalize(record.DisplayName);
        return Hash(
            "entity/v1",
            record.EntityKind,
            record.SourceKey,
            record.ExternalId ?? string.Empty,
            normalizedName,
            record.SportCode ?? string.Empty,
            record.CountryCode ?? string.Empty);
    }

    public static string Event(SourceEventRecord record, string normalizedCompetition)
    {
        var participants = record.Participants
            .OrderBy(participant => participant.Role, StringComparer.Ordinal)
            .ThenBy(participant => participant.SourceEntityKey, StringComparer.Ordinal)
            .Select(participant => $"{participant.Role}:{participant.SourceEntityKey}");

        return Hash(
            "event/v1",
            record.SourceKey,
            record.ExternalId ?? string.Empty,
            record.SportCode ?? string.Empty,
            record.KickoffAt?.ToUniversalTime().ToString("O") ?? string.Empty,
            record.Status ?? string.Empty,
            normalizedCompetition,
            string.Join('|', participants));
    }

    public static string Job(params string[] parts) => Hash(["job/v1", .. parts]);

    private static string Hash(params string[] parts)
    {
        var text = string.Join('\u001f', parts);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }
}
