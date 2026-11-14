using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Stars.Domain.Entities;
using Stars.Infrastructure.Persistence;

namespace Stars.Tests;

public class StarsDbContextModelTests
{
    private static StarsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<StarsDbContext>()
            .UseNpgsql("Host=localhost;Database=stars;Username=stars;Password=stars")
            .Options);

    private static IModel CreateDesignTimeModel()
    {
        using var db = CreateContext();

        return db.GetService<IDesignTimeModel>().Model;
    }

    [Fact]
    public void Model_maps_all_erd_tables()
    {
        var tables = CreateDesignTimeModel().GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .OfType<string>()
            .ToHashSet();

        Assert.Equal(
            new HashSet<string>
            {
                "provider_connections",
                "source_feeds",
                "sync_cursors",
                "sync_runs",
                "source_entities",
                "source_entity_names",
                "source_events",
                "source_event_participants",
                "master_entities",
                "master_names",
                "entity_relations",
                "match_candidates",
                "match_evidence",
                "match_decisions",
                "entity_links",
                "processing_jobs",
            },
            tables);
    }

    [Fact]
    public void Match_candidate_has_exactly_one_target_constraint()
    {
        var candidate = CreateDesignTimeModel().FindEntityType(typeof(MatchCandidate));

        Assert.NotNull(candidate);
        Assert.Contains(
            candidate.GetCheckConstraints(),
            constraint => constraint.Name == "ck_match_candidates_single_target");
    }

    [Fact]
    public void Entity_link_allows_one_active_link_per_source_entity()
    {
        var link = CreateDesignTimeModel().FindEntityType(typeof(EntityLink));

        Assert.NotNull(link);

        var activeLinkIndex = link.GetIndexes()
            .Single(index => index.GetDatabaseName() == "uq_entity_links_active");

        Assert.True(activeLinkIndex.IsUnique);
        Assert.Equal("valid_to IS NULL", activeLinkIndex.GetFilter());
    }
}
