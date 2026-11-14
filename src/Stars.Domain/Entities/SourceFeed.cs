namespace Stars.Domain.Entities;

/// <summary>
/// One independently synchronized stream: teams, players, competitions or fixtures.
/// </summary>
public class SourceFeed
{
    public Guid Id { get; set; }

    public Guid ProviderConnectionId { get; set; }

    public required string FeedKey { get; set; }

    public required string EntityKind { get; set; }

    public string? DatasetRef { get; set; }

    public string SyncMode { get; set; } = "snapshot";

    public required JsonDocument Config { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public ProviderConnection ProviderConnection { get; set; } = null!;

    public ICollection<SyncCursor> SyncCursors { get; set; } = [];

    public ICollection<SyncRun> SyncRuns { get; set; } = [];

    public ICollection<SourceEntity> SourceEntities { get; set; } = [];

    public ICollection<SourceEvent> SourceEvents { get; set; } = [];
}
