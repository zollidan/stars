namespace Stars.Domain.Entities;

/// <summary>
/// Durable resume point per feed. Advance only after durable local processing.
/// </summary>
public class SyncCursor
{
    public Guid Id { get; set; }

    public Guid SourceFeedId { get; set; }

    public string StreamKey { get; set; } = "default";

    public string? CursorValue { get; set; }

    public DateTimeOffset? WatermarkAt { get; set; }

    public DateTimeOffset? LastSuccessAt { get; set; }

    public DateTimeOffset? NextPollAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public SourceFeed SourceFeed { get; set; } = null!;
}
