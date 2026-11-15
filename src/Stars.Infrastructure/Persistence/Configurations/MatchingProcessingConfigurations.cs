using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stars.Domain.Entities;

namespace Stars.Infrastructure.Persistence.Configurations;

public class MatchCandidateConfiguration : IEntityTypeConfiguration<MatchCandidate>
{
    public void Configure(EntityTypeBuilder<MatchCandidate> builder)
    {
        builder.ToTable("match_candidates", table =>
            table.HasComment("Exactly one target: another source entity OR an existing master entity."));

        builder.HasKey(x => x.Id).HasName("pk_match_candidates");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.SourceEntityId).HasColumnName("source_entity_id").IsRequired();
        builder.Property(x => x.CandidateSourceEntityId).HasColumnName("candidate_source_entity_id");
        builder.Property(x => x.CandidateMasterEntityId).HasColumnName("candidate_master_entity_id");
        builder.Property(x => x.Score).HasColumnName("score").HasPrecision(6, 3);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(24).HasDefaultValue("pending").IsRequired();
        builder.Property(x => x.MatcherVersion).HasColumnName("matcher_version").HasMaxLength(60).IsRequired();
        builder.Property(x => x.InputHash).HasColumnName("input_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.GeneratedAt).HasColumnName("generated_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(x => x.SourceEntity)
            .WithMany(x => x.MatchCandidates)
            .HasForeignKey(x => x.SourceEntityId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_match_candidates_source_entity_id");

        builder.HasOne(x => x.CandidateSourceEntity)
            .WithMany()
            .HasForeignKey(x => x.CandidateSourceEntityId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_match_candidates_candidate_source_entity_id");

        builder.HasOne(x => x.CandidateMasterEntity)
            .WithMany()
            .HasForeignKey(x => x.CandidateMasterEntityId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_match_candidates_candidate_master_entity_id");

        builder.HasIndex(x => new { x.SourceEntityId, x.Status }).HasDatabaseName("ix_candidate_source_status");

        builder.HasIndex(x => new { x.Status, x.GeneratedAt }).HasDatabaseName("ix_candidate_review");

        builder.ToTable(table => table.HasCheckConstraint(
            "ck_match_candidates_single_target",
            "num_nonnulls(candidate_source_entity_id, candidate_master_entity_id) = 1"));
    }
}

public class MatchEvidenceConfiguration : IEntityTypeConfiguration<MatchEvidence>
{
    public void Configure(EntityTypeBuilder<MatchEvidence> builder)
    {
        builder.ToTable("match_evidence", table =>
            table.HasComment("Deduplicated and versioned reasoning: name similarity, shared fixture, league, rival."));

        builder.HasKey(x => x.Id).HasName("pk_match_evidence");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.MatchCandidateId).HasColumnName("match_candidate_id").IsRequired();
        builder.Property(x => x.EvidenceKey).HasColumnName("evidence_key").HasMaxLength(160).IsRequired();
        builder.Property(x => x.EvidenceType).HasColumnName("evidence_type").HasMaxLength(48).IsRequired();
        builder.Property(x => x.SourceEventId).HasColumnName("source_event_id");
        builder.Property(x => x.Weight).HasColumnName("weight").HasPrecision(8, 4);
        builder.Property(x => x.Details)
            .HasColumnName("details")
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(x => x.MatchCandidate)
            .WithMany(x => x.Evidence)
            .HasForeignKey(x => x.MatchCandidateId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_match_evidence_match_candidate_id");

        builder.HasOne(x => x.SourceEvent)
            .WithMany(x => x.MatchEvidence)
            .HasForeignKey(x => x.SourceEventId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_match_evidence_source_event_id");

        builder.HasAlternateKey(x => new { x.MatchCandidateId, x.EvidenceKey })
            .HasName("uq_candidate_evidence");
    }
}

public class MatchDecisionConfiguration : IEntityTypeConfiguration<MatchDecision>
{
    public void Configure(EntityTypeBuilder<MatchDecision> builder)
    {
        builder.ToTable("match_decisions", table =>
            table.HasComment("Immutable audit of link, reject, create, unlink and review decisions."));

        builder.HasKey(x => x.Id).HasName("pk_match_decisions");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.SourceEntityId).HasColumnName("source_entity_id").IsRequired();
        builder.Property(x => x.MatchCandidateId).HasColumnName("match_candidate_id");
        builder.Property(x => x.MasterEntityId).HasColumnName("master_entity_id");
        builder.Property(x => x.DecisionType).HasColumnName("decision_type").HasMaxLength(32).IsRequired();
        builder.Property(x => x.DecidedBy).HasColumnName("decided_by").HasMaxLength(120).IsRequired();
        builder.Property(x => x.RulesetVersion).HasColumnName("ruleset_version").HasMaxLength(60).IsRequired();
        builder.Property(x => x.Reason).HasColumnName("reason");
        builder.Property(x => x.DecisionDetails)
            .HasColumnName("decision_details")
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();
        builder.Property(x => x.DecidedAt).HasColumnName("decided_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(x => x.SourceEntity)
            .WithMany(x => x.MatchDecisions)
            .HasForeignKey(x => x.SourceEntityId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_match_decisions_source_entity_id");

        builder.HasOne(x => x.MatchCandidate)
            .WithMany(x => x.Decisions)
            .HasForeignKey(x => x.MatchCandidateId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_match_decisions_match_candidate_id");

        builder.HasOne(x => x.MasterEntity)
            .WithMany(x => x.MatchDecisions)
            .HasForeignKey(x => x.MasterEntityId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_match_decisions_master_entity_id");
    }
}

public class ProcessingJobConfiguration : IEntityTypeConfiguration<ProcessingJob>
{
    public void Configure(EntityTypeBuilder<ProcessingJob> builder)
    {
        builder.ToTable("processing_jobs", table =>
            table.HasComment("Idempotent event-driven processing; do not rematch every odds snapshot."));

        builder.HasKey(x => x.Id).HasName("pk_processing_jobs");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.SourceEntityId).HasColumnName("source_entity_id").IsRequired();
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(48).IsRequired();
        builder.Property(x => x.InputHash).HasColumnName("input_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.MatcherVersion).HasColumnName("matcher_version").HasMaxLength(60).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(24).HasDefaultValue("pending").IsRequired();
        builder.Property(x => x.Attempts).HasColumnName("attempts").HasDefaultValue(0).IsRequired();
        builder.Property(x => x.RunAfter).HasColumnName("run_after").HasDefaultValueSql("now()").IsRequired();
        builder.Property(x => x.LeaseUntil).HasColumnName("lease_until");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();

        builder.HasOne(x => x.SourceEntity)
            .WithMany(x => x.ProcessingJobs)
            .HasForeignKey(x => x.SourceEntityId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_processing_jobs_source_entity_id");

        builder.HasAlternateKey(x => new { x.SourceEntityId, x.Reason, x.InputHash, x.MatcherVersion })
            .HasName("uq_job_idempotent");

        builder.HasIndex(x => new { x.Status, x.RunAfter }).HasDatabaseName("ix_job_due");
    }
}
