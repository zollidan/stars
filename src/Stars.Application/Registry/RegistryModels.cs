namespace Stars.Application.Registry;

public sealed record SourceFeedView(
    Guid ProviderConnectionId,
    string ProviderName,
    string ConnectorKey,
    Guid SourceFeedId,
    string FeedKey,
    string EntityKind,
    string? DatasetRef,
    string SyncMode,
    bool Enabled,
    string? Cursor,
    DateTimeOffset? LastSuccessAt,
    string? LastRunStatus,
    DateTimeOffset? LastRunAt,
    long EntityCount,
    long EventCount,
    string? LastError,
    bool HasChanges);

public sealed record SourceLinkView(
    Guid SourceEntityId,
    Guid SourceFeedId,
    string FeedKey,
    string ProviderName,
    string DisplayName,
    string? ExternalId,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<string> Names);

public sealed record DirectoryEntryView(
    Guid MasterEntityId,
    string EntityKind,
    string PreferredName,
    string NormalizedName,
    string? SportCode,
    string? CountryCode,
    string Status,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Names,
    IReadOnlyList<SourceLinkView> Sources);

public sealed record EvidenceView(
    string Key,
    string EvidenceType,
    double? Weight,
    string Explanation,
    string Details);

public sealed record ReviewItemView(
    Guid CandidateId,
    Guid SourceEntityId,
    string SourceName,
    string SourceFeedKey,
    string ProviderName,
    string TargetName,
    Guid? CandidateMasterEntityId,
    Guid? CandidateSourceEntityId,
    double? Score,
    string Status,
    DateTimeOffset GeneratedAt,
    string InputHash,
    IReadOnlyList<EvidenceView> Evidence);

public sealed record ResolveView(
    Guid SourceEntityId,
    Guid SourceFeedId,
    string FeedKey,
    string EntityKind,
    string SourceKey,
    string DisplayName,
    Guid MasterEntityId,
    string MasterName,
    DateTimeOffset LinkedAt,
    decimal? Confidence);

public sealed record MasterDetailView(
    DirectoryEntryView Entity,
    IReadOnlyList<ReviewItemView> OpenReviews);

public sealed record SyncRequestResult(
    Guid SyncRunId,
    Guid SourceFeedId,
    string Status,
    long RecordsRead,
    long RecordsChanged,
    string? Error,
    bool Busy);


public sealed record MetricsView(
    long SourceEntities,
    long SourceEvents,
    long ProviderConnections,
    long SourceFeeds,
    long MasterEntities,
    long ActiveLinks,
    long PendingReviews,
    long PendingJobs,
    long ProcessingJobs,
    long DoneJobs,
    long FailedSyncRuns,
    long RecordsRead,
    double AverageSyncSeconds);
