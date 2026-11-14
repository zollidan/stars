namespace Stars.Domain.Entities;

/// <summary>
/// Semantic and time-bound relations: player member_of team, team plays_in competition.
/// </summary>
public class EntityRelation
{
    public Guid Id { get; set; }

    public Guid FromMasterEntityId { get; set; }

    public Guid ToMasterEntityId { get; set; }

    public required string RelationType { get; set; }

    public DateTimeOffset? ValidFrom { get; set; }

    public DateTimeOffset? ValidTo { get; set; }

    public required JsonDocument Attributes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public MasterEntity FromMasterEntity { get; set; } = null!;

    public MasterEntity ToMasterEntity { get; set; } = null!;
}
