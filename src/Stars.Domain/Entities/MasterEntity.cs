namespace Stars.Domain.Entities;

/// <summary>
/// Canonical entity. Equal names do not imply equal identity.
/// </summary>
public class MasterEntity
{
    public Guid Id { get; set; }

    public required string EntityKind { get; set; }

    public required string PreferredName { get; set; }

    public required string NormalizedName { get; set; }

    public string? SportCode { get; set; }

    public string? CountryCode { get; set; }

    public required JsonDocument Attributes { get; set; }

    public string Status { get; set; } = "active";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<MasterName> Names { get; set; } = [];

    public ICollection<EntityRelation> RelationsFrom { get; set; } = [];

    public ICollection<EntityRelation> RelationsTo { get; set; } = [];

    public ICollection<EntityLink> EntityLinks { get; set; } = [];

    public ICollection<MatchDecision> MatchDecisions { get; set; } = [];
}
