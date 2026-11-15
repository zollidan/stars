using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stars.Domain.Entities;

namespace Stars.Infrastructure.Persistence.Configurations;

public class ProviderConnectionConfiguration : IEntityTypeConfiguration<ProviderConnection>
{
    public void Configure(EntityTypeBuilder<ProviderConnection> builder)
    {
        builder.ToTable("provider_connections", table =>
            table.HasComment("Provider configuration. Store only a secret reference, never API keys in plaintext."));

        builder.HasKey(x => x.Id).HasName("pk_provider_connections");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
        builder.Property(x => x.ConnectorKey).HasColumnName("connector_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.BaseUrl).HasColumnName("base_url");
        builder.Property(x => x.AuthSecretRef).HasColumnName("auth_secret_ref");
        builder.Property(x => x.Settings)
            .HasColumnName("settings")
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();
        builder.Property(x => x.IsEnabled).HasColumnName("is_enabled").HasDefaultValue(true).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasIndex(x => x.ConnectorKey).HasDatabaseName("ix_providers_connector");
        builder.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_provider_connections_name");
    }
}

public class SourceFeedConfiguration : IEntityTypeConfiguration<SourceFeed>
{
    public void Configure(EntityTypeBuilder<SourceFeed> builder)
    {
        builder.ToTable("source_feeds", table =>
            table.HasComment("One independently synchronized stream: teams, players, competitions, fixtures."));

        builder.HasKey(x => x.Id).HasName("pk_source_feeds");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.ProviderConnectionId).HasColumnName("provider_connection_id").IsRequired();
        builder.Property(x => x.FeedKey).HasColumnName("feed_key").HasMaxLength(120).IsRequired();
        builder.Property(x => x.EntityKind).HasColumnName("entity_kind").HasMaxLength(24).IsRequired();
        builder.Property(x => x.DatasetRef).HasColumnName("dataset_ref");
        builder.Property(x => x.SyncMode).HasColumnName("sync_mode").HasMaxLength(24).HasDefaultValue("snapshot").IsRequired();
        builder.Property(x => x.Config)
            .HasColumnName("config")
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();
        builder.Property(x => x.IsEnabled).HasColumnName("is_enabled").HasDefaultValue(true).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(x => x.ProviderConnection)
            .WithMany(x => x.SourceFeeds)
            .HasForeignKey(x => x.ProviderConnectionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_source_feeds_provider_connection_id");

        builder.HasAlternateKey(x => new { x.ProviderConnectionId, x.FeedKey })
            .HasName("uq_feed_provider_key");
    }
}

public class SyncCursorConfiguration : IEntityTypeConfiguration<SyncCursor>
{
    public void Configure(EntityTypeBuilder<SyncCursor> builder)
    {
        builder.ToTable("sync_cursors", table =>
            table.HasComment("Durable resume point per feed; advance only after durable local processing."));

        builder.HasKey(x => x.Id).HasName("pk_sync_cursors");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.SourceFeedId).HasColumnName("source_feed_id").IsRequired();
        builder.Property(x => x.StreamKey).HasColumnName("stream_key").HasMaxLength(120).HasDefaultValue("default").IsRequired();
        builder.Property(x => x.CursorValue).HasColumnName("cursor_value");
        builder.Property(x => x.WatermarkAt).HasColumnName("watermark_at");
        builder.Property(x => x.LastSuccessAt).HasColumnName("last_success_at");
        builder.Property(x => x.NextPollAt).HasColumnName("next_poll_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(x => x.SourceFeed)
            .WithMany(x => x.SyncCursors)
            .HasForeignKey(x => x.SourceFeedId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sync_cursors_source_feed_id");

        builder.HasAlternateKey(x => new { x.SourceFeedId, x.StreamKey })
            .HasName("uq_sync_cursor");
    }
}

public class SyncRunConfiguration : IEntityTypeConfiguration<SyncRun>
{
    public void Configure(EntityTypeBuilder<SyncRun> builder)
    {
        builder.ToTable("sync_runs", table =>
            table.HasComment("Run history, failures and source health metrics."));

        builder.HasKey(x => x.Id).HasName("pk_sync_runs");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.SourceFeedId).HasColumnName("source_feed_id").IsRequired();
        builder.Property(x => x.Mode).HasColumnName("mode").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(24).IsRequired();
        builder.Property(x => x.RecordsRead).HasColumnName("records_read").HasDefaultValue(0L).IsRequired();
        builder.Property(x => x.RecordsChanged).HasColumnName("records_changed").HasDefaultValue(0L).IsRequired();
        builder.Property(x => x.ErrorMessage).HasColumnName("error_message");
        builder.Property(x => x.StartedAt).HasColumnName("started_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");

        builder.HasOne(x => x.SourceFeed)
            .WithMany(x => x.SyncRuns)
            .HasForeignKey(x => x.SourceFeedId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sync_runs_source_feed_id");

        builder.HasIndex(x => new { x.SourceFeedId, x.StartedAt }).HasDatabaseName("ix_runs_feed_started");
    }
}
