namespace Stars.Domain.Entities;

/// <summary>
/// Immutable audit of link, reject, create, unlink and review decisions.
/// </summary>
public class MatchDecision
{
    public Guid Id { get; set; }

    public Guid SourceEntityId { get; set; }

    public Guid? MatchCandidateId { get; set; }

    public Guid? MasterEntityId { get; set; }

    public required string DecisionType { get; set; }

    public required string DecidedBy { get; set; }

    public required string RulesetVersion { get; set; }

    public string? Reason { get; set; }

    public required JsonDocument DecisionDetails { get; set; }

    public DateTimeOffset DecidedAt { get; set; }

    public SourceEntity SourceEntity { get; set; } = null!;

    public MatchCandidate? MatchCandidate { get; set; }

    public MasterEntity? MasterEntity { get; set; }

    public ICollection<EntityLink> EntityLinks { get; set; } = [];
}
