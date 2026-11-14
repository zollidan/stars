namespace Stars.Domain.Entities;

/// <summary>
/// Run history, failures and source health metrics.
/// </summary>
public class SyncRun
{
    public Guid Id { get; set; }

    public Guid SourceFeedId { get; set; }

    public required string Mode { get; set; }

    public required string Status { get; set; }

    public long RecordsRead { get; set; }

    public long RecordsChanged { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public SourceFeed SourceFeed { get; set; } = null!;
}
