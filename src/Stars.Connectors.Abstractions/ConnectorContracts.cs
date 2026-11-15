using System.Text.Json;

namespace Stars.Connectors.Abstractions;

/// <summary>
/// What a connector can do. A missing capability is a normal, supported state:
/// STARS never requires a provider to expose change feeds.
/// </summary>
public sealed record ConnectorCapabilities(
    bool SupportsSnapshot,
    bool SupportsChanges,
    bool SupportsCursor,
    bool SupportsPush,
    string SyncMode,
    string? Notes = null)
{
    public static ConnectorCapabilities SnapshotOnly(string? notes = null) =>
        new(true, false, false, false, "snapshot", notes);
}

/// <summary>
/// Immutable, provider-neutral entity observation. <paramref name="SourceKey"/> must be
/// stable for as long as the provider keeps the same real-world entity, even when the
/// provider has no native identifier.
/// </summary>
public sealed record SourceEntityRecord(
    string EntityKind,
    string SourceKey,
    string? ExternalId,
    string DisplayName,
    string? SportCode,
    string? CountryCode,
    IReadOnlyList<SourceNameRecord>? Names = null,
    JsonElement? Attributes = null,
    DateTimeOffset? ObservedAt = null);

public sealed record SourceNameRecord(string Name, string? LanguageCode = null);

public sealed record SourceEventParticipantRecord(string SourceEntityKey, string Role);

/// <summary>
/// Compact fixture context. Deliberately excludes odds and full provider payloads.
/// </summary>
public sealed record SourceEventRecord(
    string SourceKey,
    string? ExternalId,
    string? SportCode,
    string? CompetitionSourceKey,
    DateTimeOffset? KickoffAt,
    string? Status,
    IReadOnlyList<SourceEventParticipantRecord> Participants,
    JsonElement? Attributes = null,
    DateTimeOffset? ObservedAt = null);

/// <summary>
/// One readable page. <paramref name="Checkpoint"/> is committed only after every record
/// in the page has been written durably.
/// </summary>
public sealed record SourceBatch(
    IReadOnlyList<SourceEntityRecord> Entities,
    IReadOnlyList<SourceEventRecord> Events,
    string? Checkpoint,
    long Sequence = 0,
    int? InputRecordCount = null)
{
    // One provider row may produce several entities and an event (for example EVA).
    public int RecordCount => InputRecordCount ?? Entities.Count + Events.Count;

    public static SourceBatch Empty(string? checkpoint = null) =>
        new([], [], checkpoint);
}

/// <summary>
/// Per-run settings handed to a connector. Secrets are resolved by the host, never stored
/// in the provider_connections table in plaintext.
/// </summary>
public sealed record ConnectorContext(
    Guid ProviderConnectionId,
    Guid SourceFeedId,
    string FeedKey,
    string EntityKind,
    string? BaseUrl,
    JsonElement Settings,
    string? Cursor,
    long MaxRecords,
    string? Secret,
    HttpClient? HttpClient = null);

public interface IConnector
{
    string Key { get; }

    ConnectorCapabilities Capabilities { get; }

    /// <summary>
    /// Reads one bounded snapshot. Implementations must stream pages rather than
    /// materialise the full provider dataset in memory.
    /// </summary>
    IAsyncEnumerable<SourceBatch> ReadSnapshotAsync(
        ConnectorContext context,
        CancellationToken cancellationToken);
}

public interface IConnectorRegistry
{
    IConnector? Resolve(string key);

    IReadOnlyCollection<IConnector> All { get; }
}

public sealed class ConnectorRegistry(IEnumerable<IConnector> connectors) : IConnectorRegistry
{
    private readonly Dictionary<string, IConnector> _connectors =
        connectors.ToDictionary(connector => connector.Key, StringComparer.OrdinalIgnoreCase);

    public IConnector? Resolve(string key) =>
        _connectors.GetValueOrDefault(key);

    public IReadOnlyCollection<IConnector> All => _connectors.Values;
}

public static class EntityKinds
{
    public const string Team = "team";
    public const string Competition = "competition";
    public const string Player = "player";
}
