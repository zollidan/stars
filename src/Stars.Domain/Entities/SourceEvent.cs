namespace Stars.Domain.Entities;

/// <summary>
/// One compact contextual fact per source fixture, not per odds snapshot.
/// </summary>
public class SourceEvent
{
    public Guid Id { get; set; }

    public Guid SourceFeedId { get; set; }

    public required string SourceKey { get; set; }

    public string? ExternalEventId { get; set; }

    public string? SportCode { get; set; }

    public Guid? CompetitionSourceEntityId { get; set; }

    public DateTimeOffset? KickoffAt { get; set; }

    public string? EventStatus { get; set; }

    public required string ContextHash { get; set; }

    public required JsonDocument Attributes { get; set; }

    public string? RawReference { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public SourceFeed SourceFeed { get; set; } = null!;

    public SourceEntity? CompetitionSourceEntity { get; set; }

    public ICollection<SourceEventParticipant> Participants { get; set; } = [];

    public ICollection<MatchEvidence> MatchEvidence { get; set; } = [];
}
