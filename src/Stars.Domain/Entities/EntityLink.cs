namespace Stars.Domain.Entities;

/// <summary>
/// One active master ID per source entity. Closed links remain for audit.
/// </summary>
public class EntityLink
{
    public Guid Id { get; set; }

    public Guid SourceEntityId { get; set; }

    public Guid MasterEntityId { get; set; }

    public Guid DecisionId { get; set; }

    public decimal? Confidence { get; set; }

    public DateTimeOffset ValidFrom { get; set; }

    public DateTimeOffset? ValidTo { get; set; }

    public SourceEntity SourceEntity { get; set; } = null!;

    public MasterEntity MasterEntity { get; set; } = null!;

    public MatchDecision Decision { get; set; } = null!;
}
