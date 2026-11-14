using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stars.Domain.Entities;

namespace Stars.Infrastructure.Persistence.Configurations;

public class MasterEntityConfiguration : IEntityTypeConfiguration<MasterEntity>
{
    public void Configure(EntityTypeBuilder<MasterEntity> builder)
    {
        builder.ToTable("master_entities", table =>
            table.HasComment("Canonical entities; same name does NOT imply same identity."));

        builder.HasKey(x => x.Id).HasName("pk_master_entities");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.EntityKind).HasColumnName("entity_kind").HasMaxLength(24).IsRequired();
        builder.Property(x => x.PreferredName).HasColumnName("preferred_name").IsRequired();
        builder.Property(x => x.NormalizedName).HasColumnName("normalized_name").IsRequired();
        builder.Property(x => x.SportCode).HasColumnName("sport_code").HasMaxLength(32);
        builder.Property(x => x.CountryCode).HasColumnName("country_code").HasColumnType("char(2)");
        builder.Property(x => x.Attributes)
            .HasColumnName("attributes")
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(24).HasDefaultValue("active").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasIndex(x => new { x.EntityKind, x.SportCode }).HasDatabaseName("ix_master_kind");
    }
}

public class MasterNameConfiguration : IEntityTypeConfiguration<MasterName>
{
    public void Configure(EntityTypeBuilder<MasterName> builder)
    {
        builder.ToTable("master_names", table =>
            table.HasComment("Approved names and aliases, distinct from raw provider names."));

        builder.HasKey(x => x.Id).HasName("pk_master_names");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.MasterEntityId).HasColumnName("master_entity_id").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").IsRequired();
        builder.Property(x => x.NormalizedName).HasColumnName("normalized_name").IsRequired();
        builder.Property(x => x.LanguageCode).HasColumnName("language_code").HasMaxLength(12);
        builder.Property(x => x.NameKind).HasColumnName("name_kind").HasMaxLength(24).HasDefaultValue("alias").IsRequired();
        builder.Property(x => x.RecordedAt).HasColumnName("recorded_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(x => x.MasterEntity)
            .WithMany(x => x.Names)
            .HasForeignKey(x => x.MasterEntityId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_master_names_master_entity_id");

        builder.HasAlternateKey(x => new { x.MasterEntityId, x.Name, x.NameKind })
            .HasName("uq_master_alias");
    }
}

public class EntityRelationConfiguration : IEntityTypeConfiguration<EntityRelation>
{
    public void Configure(EntityTypeBuilder<EntityRelation> builder)
    {
        builder.ToTable("entity_relations", table =>
            table.HasComment("Semantic and time-bound relations: player member_of team, team plays_in competition."));

        builder.HasKey(x => x.Id).HasName("pk_entity_relations");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.FromMasterEntityId).HasColumnName("from_master_entity_id").IsRequired();
        builder.Property(x => x.ToMasterEntityId).HasColumnName("to_master_entity_id").IsRequired();
        builder.Property(x => x.RelationType).HasColumnName("relation_type").HasMaxLength(48).IsRequired();
        builder.Property(x => x.ValidFrom).HasColumnName("valid_from");
        builder.Property(x => x.ValidTo).HasColumnName("valid_to");
        builder.Property(x => x.Attributes)
            .HasColumnName("attributes")
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(x => x.FromMasterEntity)
            .WithMany(x => x.RelationsFrom)
            .HasForeignKey(x => x.FromMasterEntityId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_entity_relations_from_master_entity_id");

        builder.HasOne(x => x.ToMasterEntity)
            .WithMany(x => x.RelationsTo)
            .HasForeignKey(x => x.ToMasterEntityId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_entity_relations_to_master_entity_id");
    }
}

public class EntityLinkConfiguration : IEntityTypeConfiguration<EntityLink>
{
    public void Configure(EntityTypeBuilder<EntityLink> builder)
    {
        builder.ToTable("entity_links", table =>
            table.HasComment("One active master ID per source entity. Closed links remain for audit."));

        builder.HasKey(x => x.Id).HasName("pk_entity_links");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.SourceEntityId).HasColumnName("source_entity_id").IsRequired();
        builder.Property(x => x.MasterEntityId).HasColumnName("master_entity_id").IsRequired();
        builder.Property(x => x.DecisionId).HasColumnName("decision_id").IsRequired();
        builder.Property(x => x.Confidence).HasColumnName("confidence").HasPrecision(6, 3);
        builder.Property(x => x.ValidFrom).HasColumnName("valid_from").HasDefaultValueSql("now()").IsRequired();
        builder.Property(x => x.ValidTo).HasColumnName("valid_to");

        builder.HasOne(x => x.SourceEntity)
            .WithMany(x => x.EntityLinks)
            .HasForeignKey(x => x.SourceEntityId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_entity_links_source_entity_id");

        builder.HasOne(x => x.MasterEntity)
            .WithMany(x => x.EntityLinks)
            .HasForeignKey(x => x.MasterEntityId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_entity_links_master_entity_id");

        builder.HasOne(x => x.Decision)
            .WithMany(x => x.EntityLinks)
            .HasForeignKey(x => x.DecisionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_entity_links_decision_id");

        builder.HasIndex(x => new { x.MasterEntityId, x.ValidTo }).HasDatabaseName("ix_links_master_current");

        builder.HasIndex(x => x.SourceEntityId)
            .HasDatabaseName("uq_entity_links_active")
            .IsUnique()
            .HasFilter("valid_to IS NULL");
    }
}
