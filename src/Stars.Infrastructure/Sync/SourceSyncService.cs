using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stars.Application.Configuration;
using Stars.Connectors.Abstractions;
using Stars.Domain.Entities;
using Stars.Infrastructure.Ingestion;
using Stars.Infrastructure.Persistence;

namespace Stars.Infrastructure.Sync;

public sealed record SyncFeedResult(
    Guid SyncRunId,
    Guid SourceFeedId,
    string Status,
    long RecordsRead,
    long RecordsChanged,
    string? Error = null,
    bool Busy = false);

/// <summary>
/// Runs one bounded connector snapshot and feeds it through the Source Registry.
/// Cursor commits happen per page inside the ingestion transaction; a failed run leaves
/// the previous cursor intact.
/// </summary>
public sealed class SourceSyncService(
    StarsDbContext db,
    IConnectorRegistry connectors,
    SourceIngestionStore ingestion,
    ISecretResolver secrets,
    IHttpClientFactory httpClientFactory,
    IOptions<StarsOptions> options,
    ILogger<SourceSyncService> logger)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> FeedLocks = new();

    public async Task<SyncFeedResult> SyncFeedAsync(
        Guid sourceFeedId,
        string mode,
        CancellationToken cancellationToken)
    {
        var gate = FeedLocks.GetOrAdd(sourceFeedId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken))
        {
            return new SyncFeedResult(Guid.Empty, sourceFeedId, "busy", 0, 0, "Another sync is already running for this feed.", true);
        }

        try
        {
            return await SyncFeedCoreAsync(sourceFeedId, mode, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SyncFeedResult> SyncFeedCoreAsync(
        Guid sourceFeedId,
        string mode,
        CancellationToken cancellationToken)
    {
        var feed = await db.SourceFeeds
            .AsNoTracking()
            .Include(sourceFeed => sourceFeed.ProviderConnection)
            .SingleOrDefaultAsync(sourceFeed => sourceFeed.Id == sourceFeedId, cancellationToken);

        if (feed is null)
        {
            throw new KeyNotFoundException($"Source feed {sourceFeedId} was not found.");
        }

        if (!feed.IsEnabled || !feed.ProviderConnection.IsEnabled)
        {
            return new SyncFeedResult(Guid.Empty, sourceFeedId, "disabled", 0, 0, "Provider connection or feed is disabled.");
        }

        var connector = connectors.Resolve(feed.ProviderConnection.ConnectorKey);
        if (connector is null)
        {
            return new SyncFeedResult(Guid.Empty, sourceFeedId, "failed", 0, 0,
                $"Connector '{feed.ProviderConnection.ConnectorKey}' is not registered.");
        }

        if (!connector.Capabilities.SupportsSnapshot)
        {
            return new SyncFeedResult(Guid.Empty, sourceFeedId, "failed", 0, 0,
                $"Connector '{connector.Key}' does not support snapshots.");
        }

        var settings = MergeSettings(feed.ProviderConnection.Settings, feed.Config);
        long maxRecords = options.Value.MaxRecordsPerSnapshot;
        if (settings.TryGetProperty("maxRecords", out var configuredLimit))
        {
            if (configuredLimit.ValueKind != JsonValueKind.Number
                || !configuredLimit.TryGetInt64(out var providerLimit) || providerLimit <= 0)
            {
                throw new InvalidOperationException("Provider maxRecords must be a positive integer.");
            }

            maxRecords = Math.Min(maxRecords, providerLimit);
        }

        var run = new SyncRun
        {
            SourceFeedId = feed.Id,
            Mode = mode,
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow,
        };

        db.SyncRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        var cursor = await db.SyncCursors
            .AsNoTracking()
            .Where(value => value.SourceFeedId == feed.Id && value.StreamKey == "default")
            .Select(value => value.CursorValue)
            .SingleOrDefaultAsync(cancellationToken);

        var context = new ConnectorContext(
            feed.ProviderConnectionId,
            feed.Id,
            feed.FeedKey,
            feed.EntityKind,
            feed.ProviderConnection.BaseUrl,
            settings,
            cursor,
            maxRecords,
            secrets.Resolve(feed.ProviderConnection.AuthSecretRef),
            httpClientFactory.CreateClient("connectors"));

        long recordsRead = 0;
        long recordsChanged = 0;
        var feedDescriptor = new FeedDescriptor(feed.Id, feed.FeedKey);

        try
        {
            await foreach (var batch in connector.ReadSnapshotAsync(context, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                if (batch.RecordCount < 0 || batch.RecordCount > maxRecords - recordsRead)
                {
                    throw new InvalidOperationException(
                        $"Connector '{connector.Key}' exceeded the {maxRecords} input record limit.");
                }

                var ingested = await ingestion.IngestBatchAsync(
                    feedDescriptor,
                    batch,
                    batch.Checkpoint,
                    cancellationToken);
                recordsRead += batch.RecordCount;
                // Changed already includes newly inserted records.
                recordsChanged += ingested.EntitiesChanged + ingested.EventsChanged;

                run.RecordsRead = recordsRead;
                run.RecordsChanged = recordsChanged;
                await db.SaveChangesAsync(cancellationToken);
            }

            run.Status = "succeeded";
            run.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Sync run {RunId} for feed {FeedKey} succeeded: {Records} records.",
                run.Id,
                feed.FeedKey,
                recordsRead);

            return new SyncFeedResult(run.Id, feed.Id, run.Status, recordsRead, recordsChanged);
        }
        catch (OperationCanceledException)
        {
            run.Status = "cancelled";
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.ErrorMessage = "Sync was cancelled; the last committed page remains resumable.";
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Sync run {RunId} for feed {FeedKey} failed", run.Id, feed.FeedKey);
            run.Status = "failed";
            run.ErrorMessage = Truncate(exception.Message, 2000);
            run.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return new SyncFeedResult(run.Id, feed.Id, run.Status, recordsRead, recordsChanged, exception.Message);
        }
    }

    public async Task<IReadOnlyList<SyncFeedResult>> SyncAllEnabledAsync(CancellationToken cancellationToken)
    {
        var feeds = await db.SourceFeeds
            .AsNoTracking()
            .Where(feed => feed.IsEnabled && feed.ProviderConnection.IsEnabled)
            .OrderBy(feed => feed.FeedKey)
            .Select(feed => feed.Id)
            .ToListAsync(cancellationToken);

        var results = new List<SyncFeedResult>(feeds.Count);
        foreach (var feedId in feeds)
        {
            results.Add(await SyncFeedAsync(feedId, "manual", cancellationToken));
        }

        return results;
    }

    private static JsonElement MergeSettings(JsonDocument providerSettings, JsonDocument feedConfig)
    {
        var merged = new JsonObject();
        if (providerSettings.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerSettings.RootElement.EnumerateObject())
            {
                merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }

        if (feedConfig.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in feedConfig.RootElement.EnumerateObject())
            {
                merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }

        return JsonSerializer.SerializeToElement(merged);
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];
}
