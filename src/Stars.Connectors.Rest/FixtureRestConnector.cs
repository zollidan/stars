using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stars.Connectors.Abstractions;

namespace Stars.Connectors.Rest;

/// <summary>
/// A small, honest second provider: JSON fixtures for local development and the same DTO
/// shape over HTTP for a real REST API. It deliberately supports snapshot reads only;
/// absence of ReadChanges is a normal connector state in STARS.
/// </summary>
public sealed class FixtureRestConnector : IConnector
{
    public const string ConnectorKey = "rest.fixture";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Key => ConnectorKey;

    public ConnectorCapabilities Capabilities => ConnectorCapabilities.SnapshotOnly(
        "Snapshot/fixture connector. Replays are idempotent because source keys are stable.");

    public async IAsyncEnumerable<SourceBatch> ReadSnapshotAsync(
        ConnectorContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var fixturePath = GetString(settings, "fixturePath");
        var pageSize = Math.Min(
            Math.Clamp(GetInt(settings, "pageSize") ?? 100, 1, 1000),
            (int)Math.Max(1, context.MaxRecords));

        RestSnapshot snapshot;
        if (!string.IsNullOrWhiteSpace(fixturePath))
        {
            var path = Path.IsPathRooted(fixturePath)
                ? fixturePath
                : Path.Combine(AppContext.BaseDirectory, fixturePath);
            if (!File.Exists(path))
            {
                path = Path.Combine(Directory.GetCurrentDirectory(), fixturePath);
            }

            await using var stream = File.OpenRead(path);
            snapshot = await JsonSerializer.DeserializeAsync<RestSnapshot>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException($"REST fixture '{path}' is empty.");
        }
        else
        {
            if (context.HttpClient is null || string.IsNullOrWhiteSpace(context.BaseUrl))
            {
                throw new InvalidOperationException(
                    "REST connector needs either settings.fixturePath or baseUrl + an HTTP client.");
            }

            var url = context.BaseUrl.TrimEnd('/') + "/snapshot";
            using var response = await context.HttpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            snapshot = await JsonSerializer.DeserializeAsync<RestSnapshot>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException($"REST provider '{url}' returned an empty snapshot.");
        }

        var version = string.IsNullOrWhiteSpace(snapshot.Version)
            ? "snapshot"
            : snapshot.Version.Trim();
        var entities = (snapshot.Entities ?? [])
            .OrderBy(entity => entity.Key, StringComparer.Ordinal)
            .ToArray();
        var events = (snapshot.Events ?? [])
            .OrderBy(sourceEvent => sourceEvent.Key, StringComparer.Ordinal)
            .ToArray();

        long emitted = 0;
        var sequence = 0;

        foreach (var page in entities.Chunk(pageSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            emitted += page.Length;
            sequence++;
            yield return new SourceBatch(
                page.Select(MapEntity).ToArray(),
                [],
                $"{version}:entities:{sequence}",
                sequence);

            if (emitted >= context.MaxRecords)
            {
                yield break;
            }
        }

        foreach (var page in events.Chunk(pageSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            emitted += page.Length;
            sequence++;
            yield return new SourceBatch(
                [],
                page.Select(MapEvent).ToArray(),
                $"{version}:events:{sequence}",
                sequence);

            if (emitted >= context.MaxRecords)
            {
                yield break;
            }
        }

        yield return new SourceBatch([], [], $"{version}:complete", sequence + 1);
    }

    private static SourceEntityRecord MapEntity(RestEntity entity) =>
        new(
            string.IsNullOrWhiteSpace(entity.Kind) ? EntityKinds.Team : entity.Kind,
            entity.Key,
            entity.ExternalId,
            entity.Name,
            entity.Sport,
            entity.Country,
            (entity.Aliases ?? []).Select(alias => new SourceNameRecord(alias)).ToArray(),
            entity.Attributes,
            entity.ObservedAt);

    private static SourceEventRecord MapEvent(RestEvent sourceEvent) =>
        new(
            sourceEvent.Key,
            sourceEvent.ExternalId,
            sourceEvent.Sport,
            sourceEvent.Competition,
            sourceEvent.Kickoff,
            sourceEvent.Status,
            (sourceEvent.Participants ?? [])
                .Select(participant => new SourceEventParticipantRecord(participant.Key, participant.Role))
                .ToArray(),
            sourceEvent.Attributes,
            sourceEvent.ObservedAt);

    private static string? GetString(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object
        && settings.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object
        && settings.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var number)
            ? number
            : null;
}

public sealed class RestSnapshot
{
    public string? Version { get; set; }

    public List<RestEntity>? Entities { get; set; }

    public List<RestEvent>? Events { get; set; }
}

public sealed class RestEntity
{
    public string Kind { get; set; } = EntityKinds.Team;

    public string Key { get; set; } = string.Empty;

    public string? ExternalId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Sport { get; set; }

    public string? Country { get; set; }

    public List<string>? Aliases { get; set; }

    public JsonElement? Attributes { get; set; }

    public DateTimeOffset? ObservedAt { get; set; }
}

public sealed class RestEvent
{
    public string Key { get; set; } = string.Empty;

    public string? ExternalId { get; set; }

    public string? Sport { get; set; }

    public string? Competition { get; set; }

    public DateTimeOffset? Kickoff { get; set; }

    public string? Status { get; set; }

    public List<RestParticipant>? Participants { get; set; }

    public JsonElement? Attributes { get; set; }

    public DateTimeOffset? ObservedAt { get; set; }
}

public sealed class RestParticipant
{
    public string Key { get; set; } = string.Empty;

    public string Role { get; set; } = "home";
}
