namespace Stars.Domain.Entities;

/// <summary>
/// Exactly one target: another source entity or an existing master entity.
/// </summary>
public class MatchCandidate
{
    public Guid Id { get; set; }

    public Guid SourceEntityId { get; set; }

    public Guid? CandidateSourceEntityId { get; set; }

    public Guid? CandidateMasterEntityId { get; set; }

    public decimal? Score { get; set; }

    public string Status { get; set; } = "pending";

    public required string MatcherVersion { get; set; }

    public required string InputHash { get; set; }

    public DateTimeOffset GeneratedAt { get; set; }

    public SourceEntity SourceEntity { get; set; } = null!;

    public SourceEntity? CandidateSourceEntity { get; set; }

    public MasterEntity? CandidateMasterEntity { get; set; }

    public ICollection<MatchEvidence> Evidence { get; set; } = [];

    public ICollection<MatchDecision> Decisions { get; set; } = [];
}
