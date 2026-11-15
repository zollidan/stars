using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stars.Application.Configuration;
using Stars.Domain.Entities;
using Stars.Infrastructure.Persistence;

namespace Stars.Infrastructure.Demo;

/// <summary>
/// Creates two independent provider connections from local fixture files on an empty
/// database. A real EVA DataHub address switches the EVA connector from fixture mode to
/// gRPC without touching the rest of STARS.
/// </summary>
public sealed class DemoSeeder(
    StarsDbContext db,
    IOptions<StarsOptions> options,
    IHostEnvironment environment,
    ILogger<DemoSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var demo = options.Value.Demo;
        if (!demo.Enabled || await db.ProviderConnections.AnyAsync(cancellationToken))
        {
            return;
        }

        var fixtureRoot = Path.IsPathRooted(demo.FixtureRoot)
            ? demo.FixtureRoot
            : Path.Combine(environment.ContentRootPath, demo.FixtureRoot);

        var restPath = Path.Combine(fixtureRoot, "rest-provider.json");
        var evaPath = Path.Combine(fixtureRoot, "eva", "api_sport_matches.json");
        if (!File.Exists(restPath) || !File.Exists(evaPath))
        {
            logger.LogWarning(
                "Demo fixtures not found under {FixtureRoot}; skipping seeding. REST={RestPath}, EVA={EvaPath}",
                fixtureRoot,
                restPath,
                evaPath);
            return;
        }

        var evaUsesGrpc = !string.IsNullOrWhiteSpace(demo.EvaAddress);
        var now = DateTimeOffset.UtcNow;

        var eva = new ProviderConnection
        {
            Name = "EVA DataHub",
            ConnectorKey = "eva.datahub",
            BaseUrl = demo.EvaAddress,
            AuthSecretRef = demo.EvaTokenEnvironmentVariable,
            Settings = JsonSerializer.SerializeToDocument(new
            {
                mode = evaUsesGrpc ? "grpc" : "fixture",
                address = demo.EvaAddress ?? "http://localhost:50051",
                dataset = "api_sport_matches_v1",
                fixturePath = evaPath,
                maxRecords = demo.EvaMaxRecords,
                pageSize = 100,
            }),
            IsEnabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var rest = new ProviderConnection
        {
            Name = "API Sport (fixture)",
            ConnectorKey = "rest.fixture",
            Settings = JsonSerializer.SerializeToDocument(new
            {
                fixturePath = restPath,
                pageSize = 100,
            }),
            IsEnabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var evaFeed = new SourceFeed
        {
            ProviderConnection = eva,
            FeedKey = "eva-matches",
            EntityKind = "match",
            DatasetRef = "api_sport_matches_v1",
            SyncMode = "snapshot",
            Config = JsonSerializer.SerializeToDocument(new
            {
                note = "Bounded manual import. EVA has no safe ReadChanges contract in this prototype.",
            }),
            IsEnabled = true,
            CreatedAt = now,
        };

        var restFeed = new SourceFeed
        {
            ProviderConnection = rest,
            FeedKey = "api-sport-snapshot",
            EntityKind = "match",
            SyncMode = "snapshot",
            Config = JsonSerializer.SerializeToDocument(new { }),
            IsEnabled = true,
            CreatedAt = now,
        };

        db.ProviderConnections.AddRange(eva, rest);
        db.SourceFeeds.AddRange(evaFeed, restFeed);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded demo providers: EVA ({EvaMode}) and REST fixture from {FixtureRoot}",
            evaUsesGrpc ? "gRPC" : "fixture",
            fixtureRoot);
    }
}
