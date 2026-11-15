using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stars.Domain.Entities;

namespace Stars.Infrastructure.Persistence.Configurations;

public class SourceEntityConfiguration : IEntityTypeConfiguration<SourceEntity>
{
    public void Configure(EntityTypeBuilder<SourceEntity> builder)
    {
        builder.ToTable("source_entities", table =>
            table.HasComment("Minimal searchable projection of original provider entities, not raw odds/history."));

        builder.HasKey(x => x.Id).HasName("pk_source_entities");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.SourceFeedId).HasColumnName("source_feed_id").IsRequired();
        builder.Property(x => x.EntityKind).HasColumnName("entity_kind").HasMaxLength(24).IsRequired();
        builder.Property(x => x.SourceKey)
            .HasColumnName("source_key")
            .HasComment("Stable key from connector, even if the API has no native ID")
            .IsRequired();
        builder.Property(x => x.ExternalId).HasColumnName("external_id");
        builder.Property(x => x.DisplayName).HasColumnName("display_name").IsRequired();
        builder.Property(x => x.NormalizedName).HasColumnName("normalized_name").IsRequired();
        builder.Property(x => x.SportCode).HasColumnName("sport_code").HasMaxLength(32);
        builder.Property(x => x.CountryCode).HasColumnName("country_code").HasColumnType("char(2)");
        builder.Property(x => x.Attributes)
            .HasColumnName("attributes")
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();
        builder.Property(x => x.IdentityHash).HasColumnName("identity_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(24).HasDefaultValue("new").IsRequired();
        builder.Property(x => x.FirstSeenAt).HasColumnName("first_seen_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(x => x.LastSeenAt).HasColumnName("last_seen_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(x => x.SourceFeed)
            .WithMany(x => x.SourceEntities)
            .HasForeignKey(x => x.SourceFeedId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_source_entities_source_feed_id");

        builder.HasAlternateKey(x => new { x.SourceFeedId, x.EntityKind, x.SourceKey })
            .HasName("uq_source_identity");

        builder.HasIndex(x => new { x.EntityKind, x.SportCode }).HasDatabaseName("ix_source_kind");

        builder.HasIndex(x => x.NormalizedName)
            .HasDatabaseName("ix_source_entities_name_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");
    }
}

public class SourceEntityNameConfiguration : IEntityTypeConfiguration<SourceEntityName>
{
    public void Configure(EntityTypeBuilder<SourceEntityName> builder)
    {
        builder.ToTable("source_entity_names", table =>
            table.HasComment("Observed aliases/history of provider names."));

        builder.HasKey(x => x.Id).HasName("pk_source_entity_names");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.SourceEntityId).HasColumnName("source_entity_id").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").IsRequired();
        builder.Property(x => x.NormalizedName).HasColumnName("normalized_name").IsRequired();
        builder.Property(x => x.LanguageCode).HasColumnName("language_code").HasMaxLength(12);
        builder.Property(x => x.FirstSeenAt).HasColumnName("first_seen_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(x => x.LastSeenAt).HasColumnName("last_seen_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(x => x.IsCurrent).HasColumnName("is_current").HasDefaultValue(true).IsRequired();

        builder.HasOne(x => x.SourceEntity)
            .WithMany(x => x.Names)
            .HasForeignKey(x => x.SourceEntityId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_source_entity_names_source_entity_id");

        builder.HasAlternateKey(x => new { x.SourceEntityId, x.Name })
            .HasName("uq_source_name");
    }
}

public class SourceEventConfiguration : IEntityTypeConfiguration<SourceEvent>
{
    public void Configure(EntityTypeBuilder<SourceEvent> builder)
    {
        builder.ToTable("source_events", table =>
            table.HasComment("One compact contextual fact per source fixture, not per odds snapshot."));

        builder.HasKey(x => x.Id).HasName("pk_source_events");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.SourceFeedId).HasColumnName("source_feed_id").IsRequired();
        builder.Property(x => x.SourceKey).HasColumnName("source_key").IsRequired();
        builder.Property(x => x.ExternalEventId).HasColumnName("external_event_id");
        builder.Property(x => x.SportCode).HasColumnName("sport_code").HasMaxLength(32);
        builder.Property(x => x.CompetitionSourceEntityId).HasColumnName("competition_source_entity_id");
        builder.Property(x => x.KickoffAt).HasColumnName("kickoff_at");
        builder.Property(x => x.EventStatus).HasColumnName("event_status").HasMaxLength(32);
        builder.Property(x => x.ContextHash).HasColumnName("context_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Attributes)
            .HasColumnName("attributes")
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();
        builder.Property(x => x.RawReference).HasColumnName("raw_reference");
        builder.Property(x => x.FirstSeenAt).HasColumnName("first_seen_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(x => x.LastSeenAt).HasColumnName("last_seen_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(x => x.SourceFeed)
            .WithMany(x => x.SourceEvents)
            .HasForeignKey(x => x.SourceFeedId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_source_events_source_feed_id");

        builder.HasOne(x => x.CompetitionSourceEntity)
            .WithMany(x => x.CompetitionEvents)
            .HasForeignKey(x => x.CompetitionSourceEntityId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_source_events_competition_source_entity_id");

        builder.HasAlternateKey(x => new { x.SourceFeedId, x.SourceKey })
            .HasName("uq_source_event");

        builder.HasIndex(x => new { x.SportCode, x.KickoffAt }).HasDatabaseName("ix_event_kickoff");
    }
}

public class SourceEventParticipantConfiguration : IEntityTypeConfiguration<SourceEventParticipant>
{
    public void Configure(EntityTypeBuilder<SourceEventParticipant> builder)
    {
        builder.ToTable("source_event_participants", table =>
            table.HasComment("Home/away or general participants. Use team source entity, not a string."));

        builder.HasKey(x => x.Id).HasName("pk_source_event_participants");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.SourceEventId).HasColumnName("source_event_id").IsRequired();
        builder.Property(x => x.SourceEntityId).HasColumnName("source_entity_id").IsRequired();
        builder.Property(x => x.ParticipantRole).HasColumnName("participant_role").HasMaxLength(32).IsRequired();
        builder.Property(x => x.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(x => x.SourceEvent)
            .WithMany(x => x.Participants)
            .HasForeignKey(x => x.SourceEventId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_source_event_participants_source_event_id");

        builder.HasOne(x => x.SourceEntity)
            .WithMany(x => x.EventParticipations)
            .HasForeignKey(x => x.SourceEntityId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_source_event_participants_source_entity_id");

        builder.HasAlternateKey(x => new { x.SourceEventId, x.ParticipantRole })
            .HasName("uq_event_role");
    }
}
