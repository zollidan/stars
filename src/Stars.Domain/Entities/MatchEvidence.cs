namespace Stars.Domain.Entities;

/// <summary>
/// Deduplicated and versioned reasoning: name similarity, shared fixture, league, rival.
/// </summary>
public class MatchEvidence
{
    public Guid Id { get; set; }

    public Guid MatchCandidateId { get; set; }

    public required string EvidenceKey { get; set; }

    public required string EvidenceType { get; set; }

    public Guid? SourceEventId { get; set; }

    public decimal? Weight { get; set; }

    public required JsonDocument Details { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public MatchCandidate MatchCandidate { get; set; } = null!;

    public SourceEvent? SourceEvent { get; set; }
}
