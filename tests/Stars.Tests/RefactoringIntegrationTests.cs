using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Stars.Application.Matching;
using Stars.Connectors.Abstractions;
using Stars.Infrastructure.Ingestion;
using Stars.Infrastructure.Matching;
using Stars.Infrastructure.Persistence;

namespace Stars.Tests;

public partial class StarsEndToEndTests
{
    private static WebApplicationFactory<Program> CreateFactoryWithoutWorker(string connectionString) =>
        CreateFactory(connectionString).WithWebHostBuilder(builder => builder.ConfigureServices(
            services => services.RemoveAll<IHostedService>()));

    [Fact]
    public async Task Event_replay_preserves_newer_participants_hash_and_observation_time()
    {
        await using var factory = CreateFactoryWithoutWorker(await fixture.CreateDatabaseAsync());
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StarsDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<SourceIngestionStore>();
        var feed = await db.SourceFeeds.FirstAsync();
        var descriptor = new FeedDescriptor(feed.Id, feed.FeedKey);
        var observed = DateTimeOffset.Parse("2026-11-15T12:00:00Z");
        var teams = new[]
        {
            new SourceEntityRecord("team", "replay:a", null, "Replay Alpha", "football", null),
            new SourceEntityRecord("team", "replay:b", null, "Replay Beta", "football", null),
        };
        var current = new SourceEventRecord("replay:event", null, "football", null, observed, "scheduled",
            [new("replay:a", "home"), new("replay:b", "away")], ObservedAt: observed);
        await store.IngestBatchAsync(descriptor, new(teams, [current], "current"), "current", default);
        var jobs = await db.ProcessingJobs.CountAsync();
        var original = await db.SourceEvents.AsNoTracking().SingleAsync(e => e.SourceKey == current.SourceKey);

        var stale = current with
        {
            ObservedAt = observed.AddDays(-1),
            Participants = [new("replay:b", "home"), new("replay:a", "away")],
        };
        var result = await store.IngestBatchAsync(descriptor, new([], [stale], "stale"), "stale", default);
        var stored = await db.SourceEvents.AsNoTracking().SingleAsync(e => e.Id == original.Id);
        Assert.Equal(original.ContextHash, stored.ContextHash);
        Assert.Equal(observed, stored.LastSeenAt);
        Assert.Equal(0, result.EventsChanged);
        Assert.Equal(jobs, await db.ProcessingJobs.CountAsync());
        var home = await db.SourceEventParticipants.Where(p => p.SourceEventId == stored.Id && p.ParticipantRole == "home")
            .Select(p => p.SourceEntity.SourceKey).SingleAsync();
        Assert.Equal("replay:a", home);

        var newer = current with { ObservedAt = observed.AddDays(1) };
        result = await store.IngestBatchAsync(descriptor, new([], [newer], null), null, default);
        Assert.Equal(0, result.EventsChanged);
        Assert.Equal(jobs, await db.ProcessingJobs.CountAsync());
        stored = await db.SourceEvents.AsNoTracking().SingleAsync(e => e.Id == original.Id);
        Assert.Equal(newer.ObservedAt, stored.LastSeenAt);

        await store.IngestBatchAsync(descriptor, new([], [current], null), null, default);
        stored = await db.SourceEvents.AsNoTracking().SingleAsync(e => e.Id == original.Id);
        Assert.Equal(newer.ObservedAt, stored.LastSeenAt);
    }

    [Fact]
    public async Task Matching_uses_latest_twelve_context_facts_for_each_source()
    {
        await using var factory = CreateFactoryWithoutWorker(await fixture.CreateDatabaseAsync());
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StarsDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<SourceIngestionStore>();
        var feeds = await db.SourceFeeds.OrderBy(f => f.FeedKey).ToArrayAsync();
        var kickoff = DateTimeOffset.Parse("2026-11-15T12:00:00Z");
        for (var side = 0; side < 2; side++)
        {
            SourceEntityRecord[] entities =
            [
                new("team", "context:team", null, "Audit Context United", "football", null),
                new("team", "context:opponent", null, side == 0 ? "Braga" : "Chelsea", "football", null),
                new("team", "context:shared", null, "Liverpool", "football", null),
            ];
            var events = Enumerable.Range(0, 13).Select(index => new SourceEventRecord(
                $"context:{index}", null, "football", null, kickoff.AddDays(-index), null,
                [new("context:team", "home"), new(index == 12 ? "context:shared" : "context:opponent", "away")]))
                .ToArray();
            await store.IngestBatchAsync(new(feeds[side].Id, feeds[side].FeedKey), new(entities, events, null), null, default);
        }

        var sourceId = await db.SourceEntities.Where(e => e.SourceFeedId == feeds[0].Id && e.SourceKey == "context:team")
            .Select(e => e.Id).SingleAsync();
        var processor = scope.ServiceProvider.GetRequiredService<MatchingProcessor>();
        var result = await processor.ProcessAsync(sourceId, "context-old", default);
        Assert.False(result.Linked);
        Assert.Equal(DecisionReasons.BelowThreshold, result.Reason);
        Assert.Equal(0, Assert.Single(result.Candidates).ContextValue);

        foreach (var feed in feeds)
        {
            var recent = new SourceEventRecord("context:recent", null, "football", null, kickoff.AddDays(1), null,
                [new("context:team", "home"), new("context:shared", "away")]);
            await store.IngestBatchAsync(new(feed.Id, feed.FeedKey), new([], [recent], null), null, default);
        }

        result = await processor.ProcessAsync(sourceId, "context-recent", default);
        Assert.True(result.Linked);
        Assert.Equal(DecisionReasons.AutoLink, result.Reason);
    }

    [Theory]
    [InlineData("sources/{0}/sync")]
    [InlineData("review/{0}/confirm")]
    [InlineData("review/{0}/reject")]
    [InlineData("masters/{0}/unlink/{0}")]
    public async Task Missing_resources_return_problem_details(string route)
    {
        await using var factory = CreateFactoryWithoutWorker(await fixture.CreateDatabaseAsync());
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync($"/api/{string.Format(route, Guid.NewGuid())}", new { reviewer = "test" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, problem.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Review_reject_confirm_conflict_and_unlink_preserve_audit()
    {
        await using var factory = CreateFactory(await fixture.CreateDatabaseAsync());
        using var client = factory.CreateClient();
        await SyncInitialAsync(client);
        using var review = JsonDocument.Parse(await client.GetStringAsync("/api/review"));
        var candidate = review.RootElement.EnumerateArray().First();
        var candidateId = candidate.GetProperty("candidateId").GetString();
        using var rejected = await client.PostAsJsonAsync($"/api/review/{candidateId}/reject", new { reviewer = "test" });
        rejected.EnsureSuccessStatusCode();
        using var confirmRejected = await client.PostAsJsonAsync($"/api/review/{candidateId}/confirm", new { reviewer = "test" });
        Assert.Equal(HttpStatusCode.BadRequest, confirmRejected.StatusCode);

        using var directory = JsonDocument.Parse(await client.GetStringAsync("/api/directory"));
        var master = directory.RootElement.EnumerateArray().First();
        var masterId = master.GetProperty("masterEntityId").GetGuid();
        var sourceId = master.GetProperty("sources")[0].GetProperty("sourceEntityId").GetGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StarsDbContext>();
        var otherMasterId = await db.MasterEntities.Where(m => m.Id != masterId).Select(m => m.Id).FirstAsync();
        var conflict = new Stars.Domain.Entities.MatchCandidate
        {
            SourceEntityId = sourceId, CandidateMasterEntityId = otherMasterId,
            Status = "pending", MatcherVersion = MatchEngine.Version, InputHash = "conflict-test",
        };
        db.MatchCandidates.Add(conflict);
        await db.SaveChangesAsync();
        using var conflicting = await client.PostAsJsonAsync($"/api/review/{conflict.Id}/confirm", new { reviewer = "test" });
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);

        using var unlink = await client.PostAsJsonAsync($"/api/masters/{masterId}/unlink/{sourceId}", new { reviewer = "test", reason = "audit-test" });
        unlink.EnsureSuccessStatusCode();
        var decision = await db.MatchDecisions.AsNoTracking().SingleAsync(d => d.SourceEntityId == sourceId && d.DecisionType == "unlink");
        var link = await db.EntityLinks.AsNoTracking().SingleAsync(l => l.SourceEntityId == sourceId);
        Assert.NotNull(link.ValidTo);
        Assert.Equal(link.Id, decision.DecisionDetails.RootElement.GetProperty("link_id").GetGuid());
        Assert.Equal("audit-test", decision.Reason);
    }

    [Fact]
    public async Task Sync_applies_provider_limit_and_commits_completion_checkpoint()
    {
        await using var factory = CreateFactory(await fixture.CreateDatabaseAsync()).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Stars:Providers:eva:Settings:maxRecords", "3");
            builder.UseSetting("Stars:MaxRecordsPerSnapshot", "5");
        });
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StarsDbContext>();
        var feed = await db.SourceFeeds.SingleAsync(f => f.FeedKey == "eva-matches");
        using var response = await client.PostAsync($"/api/sources/{feed.Id}/sync", null);
        response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, result.RootElement.GetProperty("recordsRead").GetInt64());
        Assert.Equal(3, await db.SourceEvents.CountAsync(e => e.SourceFeedId == feed.Id));
        var checkpoint = await db.SyncCursors.Where(c => c.SourceFeedId == feed.Id).Select(c => c.CursorValue).SingleAsync();
        Assert.EndsWith(":3:complete", checkpoint);
    }

    [Fact]
    public async Task Oversized_page_fails_without_writing_records_or_advancing_checkpoint()
    {
        await using var factory = CreateFactoryWithoutWorker(await fixture.CreateDatabaseAsync())
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Stars:MaxRecordsPerSnapshot", "1");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IConnector>();
                    services.AddSingleton<IConnector, OversizedConnector>();
                });
            });
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StarsDbContext>();
        var feed = await db.SourceFeeds.SingleAsync(f => f.FeedKey == "api-sport-snapshot");
        using var response = await client.PostAsync($"/api/sources/{feed.Id}/sync", null);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await db.SourceEntities.ToListAsync());
        Assert.Empty(await db.SyncCursors.ToListAsync());
        Assert.Equal("failed", await db.SyncRuns.Select(r => r.Status).SingleAsync());
    }

    private sealed class OversizedConnector : IConnector
    {
        public string Key => TestSnapshotConnector.ConnectorKey;
        public ConnectorCapabilities Capabilities => ConnectorCapabilities.SnapshotOnly();

        public async IAsyncEnumerable<SourceBatch> ReadSnapshotAsync(ConnectorContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new([
                new("team", "a", null, "Alpha", null, null),
                new("team", "b", null, "Beta", null, null),
            ], [], "unsafe-checkpoint");
        }
    }
}
