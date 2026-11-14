namespace Stars.Domain.Entities;

/// <summary>
/// Observed aliases and name history of provider entities.
/// </summary>
public class SourceEntityName
{
    public Guid Id { get; set; }

    public Guid SourceEntityId { get; set; }

    public required string Name { get; set; }

    public required string NormalizedName { get; set; }

    public string? LanguageCode { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public bool IsCurrent { get; set; } = true;

    public SourceEntity SourceEntity { get; set; } = null!;
}
