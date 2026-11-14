namespace Stars.Domain.Entities;

/// <summary>
/// Home/away or general participants. References a team source entity, never a bare string.
/// </summary>
public class SourceEventParticipant
{
    public Guid Id { get; set; }

    public Guid SourceEventId { get; set; }

    public Guid SourceEntityId { get; set; }

    public required string ParticipantRole { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public SourceEvent SourceEvent { get; set; } = null!;

    public SourceEntity SourceEntity { get; set; } = null!;
}
