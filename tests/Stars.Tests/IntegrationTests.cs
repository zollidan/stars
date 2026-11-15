using Microsoft.AspNetCore.Hosting;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Stars.Api.Grpc;
using Stars.Connectors.Abstractions;
using Stars.Connectors.Rest;
using Stars.Domain.Entities;
using Stars.Infrastructure.Ingestion;
using Stars.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Stars.Tests;

public sealed class PostgresContainerFixture : IAsyncLifetime
{
    public PostgreSqlContainer? Container { get; private set; }

    public async Task InitializeAsync()
    {
        Container = new PostgreSqlBuilder("postgres:18-alpine").Build();
        await Container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (Container is not null)
        {
            await Container.DisposeAsync();
        }
    }

    public async Task<string> CreateDatabaseAsync()
    {
        if (Container is null)
        {
            throw new InvalidOperationException("PostgreSQL container is unavailable.");
        }

        var database = $"stars_test_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(Container.GetConnectionString())
        {
            Database = "postgres",
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {database}", connection);
        await command.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(Container.GetConnectionString())
        {
            Database = database,
        }.ConnectionString;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresContainerFixture>;

[Collection("postgres")]
public partial class StarsEndToEndTests(PostgresContainerFixture fixture)
{
    [Fact]
    public async Task Two_providers_link_spartak_and_review_dynamo()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await using var factory = CreateFactory(connectionString);
        var client = factory.CreateClient();
        await SyncInitialAsync(client);

        using var directory = JsonDocument.Parse(await client.GetStringAsync("/api/directory"));
        var masters = directory.RootElement.EnumerateArray().ToArray();
        Assert.Equal(4, masters.Length);

        var spartak = masters.Single(master =>
            master.GetProperty("names").EnumerateArray().Any(name => name.GetString() == "Спартак Москва"));
        Assert.Equal("Spartak Moscow", spartak.GetProperty("preferredName").GetString());
        Assert.Equal(2, spartak.GetProperty("sources").GetArrayLength());
        Assert.Contains(
            spartak.GetProperty("sources").EnumerateArray(),
            source => source.GetProperty("providerName").GetString() == "EVA DataHub");
        Assert.Contains(
            spartak.GetProperty("sources").EnumerateArray(),
            source => source.GetProperty("providerName").GetString() == "API Sport");

        using var review = JsonDocument.Parse(await client.GetStringAsync("/api/review"));
        var reviewItems = review.RootElement.EnumerateArray().ToArray();
        Assert.Contains(reviewItems, item => item.GetProperty("sourceName").GetString() == "Динамо");
        Assert.DoesNotContain(reviewItems, item => item.GetProperty("sourceName").GetString() == "Спартак Москва");

        using var sources = JsonDocument.Parse(await client.GetStringAsync("/api/sources"));
        var evaFeed = sources.RootElement.EnumerateArray()
            .Single(source => source.GetProperty("feedKey").GetString() == "eva-matches")
            .GetProperty("sourceFeedId").GetString();

        using var resolved = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/resolve?feedId={evaFeed}&kind=team&sourceKey=team%3A10"));
        Assert.Equal(
            spartak.GetProperty("masterEntityId").GetString(),
            resolved.RootElement.GetProperty("masterEntityId").GetString());
    }

    [Fact]
    public async Task Repeated_snapshots_do_not_duplicate_records_or_links()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await using var factory = CreateFactory(connectionString);
        var client = factory.CreateClient();
        await SyncInitialAsync(client);

        var before = await SnapshotAsync(client);

        var first = await client.PostAsync("/api/sync", null);
        var second = await client.PostAsync("/api/sync", null);
        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        await WaitForWorkerAsync(client);

        var after = await SnapshotAsync(client);
        Assert.Equal(before.MasterIds, after.MasterIds);
        Assert.Equal(before.LinkIds, after.LinkIds);
        Assert.Equal(before.ReviewIds, after.ReviewIds);
        Assert.Equal(before.EntityCounts, after.EntityCounts);
        Assert.Equal(4, after.MasterIds.Length);
    }

    [Fact]
    public async Task Confirming_ambiguous_review_creates_one_canonical_team()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await using var factory = CreateFactory(connectionString);
        var client = factory.CreateClient();
        await SyncInitialAsync(client);

        using var review = JsonDocument.Parse(await client.GetStringAsync("/api/review"));
        var candidate = review.RootElement.EnumerateArray().Single(item =>
            item.GetProperty("sourceName").GetString() == "Динамо"
            && item.GetProperty("targetName").GetString() == "Dynamo Moscow");

        var response = await client.PostAsJsonAsync(
            $"/api/review/{candidate.GetProperty("candidateId").GetString()}/confirm",
            new { reviewer = "test" });
        response.EnsureSuccessStatusCode();
        using var confirmed = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var masterId = confirmed.RootElement.GetProperty("masterEntityId").GetString();

        using var repeated = await client.PostAsJsonAsync(
            $"/api/review/{candidate.GetProperty("candidateId").GetString()}/confirm",
            new { reviewer = "test" });
        repeated.EnsureSuccessStatusCode();
        using var repeatedResult = JsonDocument.Parse(await repeated.Content.ReadAsStringAsync());
        Assert.Equal(masterId, repeatedResult.RootElement.GetProperty("masterEntityId").GetString());

        using var directory = JsonDocument.Parse(await client.GetStringAsync("/api/directory"));
        var master = directory.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("masterEntityId").GetString() == masterId);

        Assert.Equal("Dynamo Moscow", master.GetProperty("preferredName").GetString());
        Assert.Contains(
            master.GetProperty("names").EnumerateArray(),
            name => name.GetString() == "Динамо");
        Assert.Equal(2, master.GetProperty("sources").GetArrayLength());
        Assert.Contains(
            master.GetProperty("sources").EnumerateArray(),
            source => source.GetProperty("providerName").GetString() == "EVA DataHub");
    }

    [Fact]
    public async Task Grpc_read_api_resolves_source_and_returns_master()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await using var factory = CreateFactory(connectionString);
        var client = factory.CreateClient();
        await SyncInitialAsync(client);

        using var sources = JsonDocument.Parse(await client.GetStringAsync("/api/sources"));
        var evaFeed = sources.RootElement.EnumerateArray()
            .Single(source => source.GetProperty("feedKey").GetString() == "eva-matches")
            .GetProperty("sourceFeedId").GetString()!;

        using var channel = GrpcChannel.ForAddress(
            client.BaseAddress!,
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        var registry = new StarsRegistry.StarsRegistryClient(channel);
        var resolved = await registry.ResolveBySourceIdAsync(new ResolveBySourceIdRequest
        {
            SourceFeedId = evaFeed,
            EntityKind = "team",
            SourceKey = "team:10",
        });

        Assert.True(resolved.Found);
        Assert.Equal("Spartak Moscow", resolved.MasterName);

        var master = await registry.GetMasterEntityAsync(new GetMasterEntityRequest
        {
            MasterEntityId = resolved.MasterEntityId,
        });
        Assert.True(master.Found);
        Assert.Equal(2, master.Sources.Count);
    }

    [Fact]
    public async Task Delayed_observation_does_not_overwrite_newer_name()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<StarsDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using var db = new StarsDbContext(options);
        await db.Database.MigrateAsync();

        var provider = new ProviderConnection
        {
            Name = "delayed-test",
            ConnectorKey = RestSnapshotConnector.ConnectorKey,
            Settings = JsonSerializer.SerializeToDocument(new { }),
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var feed = new SourceFeed
        {
            ProviderConnection = provider,
            FeedKey = "delayed",
            EntityKind = "team",
            SyncMode = "snapshot",
            Config = JsonSerializer.SerializeToDocument(new { }),
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ProviderConnections.Add(provider);
        db.SourceFeeds.Add(feed);
        await db.SaveChangesAsync();

        await using var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        var store = new SourceIngestionStore(dataSource, NullLogger<SourceIngestionStore>.Instance);
        var descriptor = new FeedDescriptor(
            feed.Id, feed.FeedKey);

        var newer = new SourceEntityRecord(
            "team", "team:1", "1", "New Name", "football", "RU",
            ObservedAt: new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        await store.IngestBatchAsync(descriptor, new SourceBatch([newer], [], null), null, CancellationToken.None);

        var older = newer with
        {
            DisplayName = "Old Name",
            ObservedAt = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero),
        };
        await store.IngestBatchAsync(descriptor, new SourceBatch([older], [], null), null, CancellationToken.None);

        db.ChangeTracker.Clear();
        var stored = await db.SourceEntities.SingleAsync(entity => entity.SourceKey == "team:1");
        Assert.Equal("New Name", stored.DisplayName);
        Assert.Equal(1, await db.ProcessingJobs.CountAsync());
    }

    private static async Task<IdempotencySnapshot> SnapshotAsync(HttpClient client)
    {
        using var directory = JsonDocument.Parse(await client.GetStringAsync("/api/directory"));
        using var review = JsonDocument.Parse(await client.GetStringAsync("/api/review"));
        using var sources = JsonDocument.Parse(await client.GetStringAsync("/api/sources"));

        var masterIds = directory.RootElement.EnumerateArray()
            .Select(master => master.GetProperty("masterEntityId").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var linkIds = directory.RootElement.EnumerateArray()
            .SelectMany(master => master.GetProperty("sources").EnumerateArray())
            .Select(source => source.GetProperty("sourceEntityId").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var reviewIds = review.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("candidateId").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var entityCounts = sources.RootElement.EnumerateArray()
            .Select(source => $"{source.GetProperty("feedKey").GetString()}:{source.GetProperty("entityCount").GetInt64()}/{source.GetProperty("eventCount").GetInt64()}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new IdempotencySnapshot(masterIds, linkIds, reviewIds, entityCounts);
    }

    private static async Task SyncInitialAsync(HttpClient client)
    {
        using var response = await client.PostAsync("/api/sync", null);
        response.EnsureSuccessStatusCode();
        await WaitForWorkerAsync(client);
    }

    private static async Task WaitForWorkerAsync(HttpClient client)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var sources = JsonDocument.Parse(await client.GetStringAsync("/api/sources"));
            if (sources.RootElement.EnumerateArray().All(source => !source.GetProperty("hasChanges").GetBoolean()))
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The processing worker did not drain the queue in time.");
    }

    private sealed record IdempotencySnapshot(
        string[] MasterIds,
        string[] LinkIds,
        string[] ReviewIds,
        string[] EntityCounts);

    private static WebApplicationFactory<Program> CreateFactory(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Stars", connectionString);
            builder.UseSetting("Stars:AutoMigrate", "true");
            builder.UseSetting("Stars:Providers:eva:Name", "EVA DataHub");
            builder.UseSetting("Stars:Providers:eva:ConnectorKey", TestSnapshotConnector.ConnectorKey);
            builder.UseSetting("Stars:Providers:eva:Settings:dataset", "api_sport_matches_v1");
            builder.UseSetting("Stars:Providers:eva:Feeds:eva-matches:EntityKind", "match");
            builder.UseSetting("Stars:Providers:eva:Feeds:eva-matches:SyncMode", "snapshot");
            builder.UseSetting("Stars:Providers:rest:Name", "API Sport");
            builder.UseSetting("Stars:Providers:rest:ConnectorKey", TestSnapshotConnector.ConnectorKey);
            builder.UseSetting("Stars:Providers:rest:Feeds:api-sport-snapshot:EntityKind", "match");
            builder.UseSetting("Stars:Providers:rest:Feeds:api-sport-snapshot:SyncMode", "snapshot");
            builder.ConfigureServices(services => services.AddSingleton<IConnector, TestSnapshotConnector>());
            builder.UseEnvironment("Testing");
        });
}
