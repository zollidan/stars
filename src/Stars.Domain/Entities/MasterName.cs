namespace Stars.Domain.Entities;

/// <summary>
/// Approved names and aliases, distinct from raw provider names.
/// </summary>
public class MasterName
{
    public Guid Id { get; set; }

    public Guid MasterEntityId { get; set; }

    public required string Name { get; set; }

    public required string NormalizedName { get; set; }

    public string? LanguageCode { get; set; }

    public string NameKind { get; set; } = "alias";

    public DateTimeOffset RecordedAt { get; set; }

    public MasterEntity MasterEntity { get; set; } = null!;
}
