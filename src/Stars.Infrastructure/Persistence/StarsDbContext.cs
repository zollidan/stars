using Microsoft.EntityFrameworkCore;
using Stars.Domain.Entities;

namespace Stars.Infrastructure.Persistence;

public class StarsDbContext(DbContextOptions<StarsDbContext> options) : DbContext(options)
{
    public DbSet<ProviderConnection> ProviderConnections => Set<ProviderConnection>();

    public DbSet<SourceFeed> SourceFeeds => Set<SourceFeed>();

    public DbSet<SyncCursor> SyncCursors => Set<SyncCursor>();

    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    public DbSet<SourceEntity> SourceEntities => Set<SourceEntity>();

    public DbSet<SourceEntityName> SourceEntityNames => Set<SourceEntityName>();

    public DbSet<SourceEvent> SourceEvents => Set<SourceEvent>();

    public DbSet<SourceEventParticipant> SourceEventParticipants => Set<SourceEventParticipant>();

    public DbSet<MasterEntity> MasterEntities => Set<MasterEntity>();

    public DbSet<MasterName> MasterNames => Set<MasterName>();

    public DbSet<EntityRelation> EntityRelations => Set<EntityRelation>();

    public DbSet<EntityLink> EntityLinks => Set<EntityLink>();

    public DbSet<MatchCandidate> MatchCandidates => Set<MatchCandidate>();

    public DbSet<MatchEvidence> MatchEvidence => Set<MatchEvidence>();

    public DbSet<MatchDecision> MatchDecisions => Set<MatchDecision>();

    public DbSet<ProcessingJob> ProcessingJobs => Set<ProcessingJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(StarsDbContext).Assembly);
    }
}
