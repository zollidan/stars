namespace Stars.Domain.Entities;

/// <summary>
/// Minimal searchable projection of original provider entities, not raw odds or history.
/// </summary>
public class SourceEntity
{
    public Guid Id { get; set; }

    public Guid SourceFeedId { get; set; }

    public required string EntityKind { get; set; }

    /// <summary>Stable key from the connector, even when the API has no native identifier.</summary>
    public required string SourceKey { get; set; }

    public string? ExternalId { get; set; }

    public required string DisplayName { get; set; }

    public required string NormalizedName { get; set; }

    public string? SportCode { get; set; }

    public string? CountryCode { get; set; }

    public required JsonDocument Attributes { get; set; }

    public required string IdentityHash { get; set; }

    public string Status { get; set; } = "new";

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public SourceFeed SourceFeed { get; set; } = null!;

    public ICollection<SourceEntityName> Names { get; set; } = [];

    public ICollection<SourceEvent> CompetitionEvents { get; set; } = [];

    public ICollection<SourceEventParticipant> EventParticipations { get; set; } = [];

    public ICollection<MatchCandidate> MatchCandidates { get; set; } = [];

    public ICollection<MatchDecision> MatchDecisions { get; set; } = [];

    public ICollection<EntityLink> EntityLinks { get; set; } = [];

    public ICollection<ProcessingJob> ProcessingJobs { get; set; } = [];
}
