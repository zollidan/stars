namespace Stars.Domain.Entities;

/// <summary>
/// Idempotent event-driven processing. Do not rematch on every odds snapshot.
/// </summary>
public class ProcessingJob
{
    public Guid Id { get; set; }

    public Guid SourceEntityId { get; set; }

    public required string Reason { get; set; }

    public required string InputHash { get; set; }

    public required string MatcherVersion { get; set; }

    public string Status { get; set; } = "pending";

    public int Attempts { get; set; }

    public DateTimeOffset RunAfter { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public SourceEntity SourceEntity { get; set; } = null!;
}
