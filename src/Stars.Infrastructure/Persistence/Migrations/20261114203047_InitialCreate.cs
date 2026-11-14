using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stars.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "master_entities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    entity_kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    preferred_name = table.Column<string>(type: "text", nullable: false),
                    normalized_name = table.Column<string>(type: "text", nullable: false),
                    sport_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    country_code = table.Column<string>(type: "char(2)", nullable: true),
                    attributes = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, defaultValue: "active"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_master_entities", x => x.id);
                },
                comment: "Canonical entities; same name does NOT imply same identity.");

            migrationBuilder.CreateTable(
                name: "provider_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    connector_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    base_url = table.Column<string>(type: "text", nullable: true),
                    auth_secret_ref = table.Column<string>(type: "text", nullable: true),
                    settings = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_connections", x => x.id);
                },
                comment: "Provider configuration. Store only a secret reference, never API keys in plaintext.");

            migrationBuilder.CreateTable(
                name: "entity_relations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    from_master_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_master_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relation_type = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attributes = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entity_relations", x => x.id);
                    table.ForeignKey(
                        name: "fk_entity_relations_from_master_entity_id",
                        column: x => x.from_master_entity_id,
                        principalTable: "master_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_entity_relations_to_master_entity_id",
                        column: x => x.to_master_entity_id,
                        principalTable: "master_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Semantic and time-bound relations: player member_of team, team plays_in competition.");

            migrationBuilder.CreateTable(
                name: "master_names",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    master_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    normalized_name = table.Column<string>(type: "text", nullable: false),
                    language_code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    name_kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, defaultValue: "alias"),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_master_names", x => x.id);
                    table.UniqueConstraint("uq_master_alias", x => new { x.master_entity_id, x.name, x.name_kind });
                    table.ForeignKey(
                        name: "fk_master_names_master_entity_id",
                        column: x => x.master_entity_id,
                        principalTable: "master_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Approved names and aliases, distinct from raw provider names.");

            migrationBuilder.CreateTable(
                name: "source_feeds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    provider_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    feed_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    entity_kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    dataset_ref = table.Column<string>(type: "text", nullable: true),
                    sync_mode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, defaultValue: "snapshot"),
                    config = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_feeds", x => x.id);
                    table.UniqueConstraint("uq_feed_provider_key", x => new { x.provider_connection_id, x.feed_key });
                    table.ForeignKey(
                        name: "fk_source_feeds_provider_connection_id",
                        column: x => x.provider_connection_id,
                        principalTable: "provider_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "One independently synchronized stream: teams, players, competitions, fixtures.");

            migrationBuilder.CreateTable(
                name: "source_entities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    source_feed_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    source_key = table.Column<string>(type: "text", nullable: false, comment: "Stable key from connector, even if the API has no native ID"),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    normalized_name = table.Column<string>(type: "text", nullable: false),
                    sport_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    country_code = table.Column<string>(type: "char(2)", nullable: true),
                    attributes = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    identity_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, defaultValue: "new"),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_entities", x => x.id);
                    table.UniqueConstraint("uq_source_identity", x => new { x.source_feed_id, x.entity_kind, x.source_key });
                    table.ForeignKey(
                        name: "fk_source_entities_source_feed_id",
                        column: x => x.source_feed_id,
                        principalTable: "source_feeds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Minimal searchable projection of original provider entities, not raw odds/history.");

            migrationBuilder.CreateTable(
                name: "sync_cursors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    source_feed_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stream_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false, defaultValue: "default"),
                    cursor_value = table.Column<string>(type: "text", nullable: true),
                    watermark_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_poll_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_cursors", x => x.id);
                    table.UniqueConstraint("uq_sync_cursor", x => new { x.source_feed_id, x.stream_key });
                    table.ForeignKey(
                        name: "fk_sync_cursors_source_feed_id",
                        column: x => x.source_feed_id,
                        principalTable: "source_feeds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Durable resume point per feed; advance only after durable local processing.");

            migrationBuilder.CreateTable(
                name: "sync_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    source_feed_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    records_read = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    records_changed = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_sync_runs_source_feed_id",
                        column: x => x.source_feed_id,
                        principalTable: "source_feeds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Run history, failures and source health metrics.");

            migrationBuilder.CreateTable(
                name: "match_candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    source_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_source_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    candidate_master_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    score = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, defaultValue: "pending"),
                    matcher_version = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    input_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_match_candidates", x => x.id);
                    table.CheckConstraint("ck_match_candidates_single_target", "num_nonnulls(candidate_source_entity_id, candidate_master_entity_id) = 1");
                    table.ForeignKey(
                        name: "fk_match_candidates_candidate_master_entity_id",
                        column: x => x.candidate_master_entity_id,
                        principalTable: "master_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_match_candidates_candidate_source_entity_id",
                        column: x => x.candidate_source_entity_id,
                        principalTable: "source_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_match_candidates_source_entity_id",
                        column: x => x.source_entity_id,
                        principalTable: "source_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Exactly one target: another source entity OR an existing master entity.");

            migrationBuilder.CreateTable(
                name: "processing_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    source_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    input_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    matcher_version = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, defaultValue: "pending"),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    run_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processing_jobs", x => x.id);
                    table.UniqueConstraint("uq_job_idempotent", x => new { x.source_entity_id, x.reason, x.input_hash, x.matcher_version });
                    table.ForeignKey(
                        name: "fk_processing_jobs_source_entity_id",
                        column: x => x.source_entity_id,
                        principalTable: "source_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Idempotent event-driven processing; do not rematch every odds snapshot.");

            migrationBuilder.CreateTable(
                name: "source_entity_names",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    source_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    normalized_name = table.Column<string>(type: "text", nullable: false),
                    language_code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    is_current = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_entity_names", x => x.id);
                    table.UniqueConstraint("uq_source_name", x => new { x.source_entity_id, x.name });
                    table.ForeignKey(
                        name: "fk_source_entity_names_source_entity_id",
                        column: x => x.source_entity_id,
                        principalTable: "source_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Observed aliases/history of provider names.");

            migrationBuilder.CreateTable(
                name: "source_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    source_feed_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_key = table.Column<string>(type: "text", nullable: false),
                    external_event_id = table.Column<string>(type: "text", nullable: true),
                    sport_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    competition_source_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kickoff_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    event_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    context_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    attributes = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    raw_reference = table.Column<string>(type: "text", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_events", x => x.id);
                    table.UniqueConstraint("uq_source_event", x => new { x.source_feed_id, x.source_key });
                    table.ForeignKey(
                        name: "fk_source_events_competition_source_entity_id",
                        column: x => x.competition_source_entity_id,
                        principalTable: "source_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_source_events_source_feed_id",
                        column: x => x.source_feed_id,
                        principalTable: "source_feeds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "One compact contextual fact per source fixture, not per odds snapshot.");

            migrationBuilder.CreateTable(
                name: "match_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    source_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    match_candidate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    master_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decision_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    decided_by = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ruleset_version = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    decision_details = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_match_decisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_match_decisions_master_entity_id",
                        column: x => x.master_entity_id,
                        principalTable: "master_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_match_decisions_match_candidate_id",
                        column: x => x.match_candidate_id,
                        principalTable: "match_candidates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_match_decisions_source_entity_id",
                        column: x => x.source_entity_id,
                        principalTable: "source_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Immutable audit of link, reject, create, unlink and review decisions.");

            migrationBuilder.CreateTable(
                name: "match_evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    match_candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evidence_key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    evidence_type = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    weight = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: true),
                    details = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_match_evidence", x => x.id);
                    table.UniqueConstraint("uq_candidate_evidence", x => new { x.match_candidate_id, x.evidence_key });
                    table.ForeignKey(
                        name: "fk_match_evidence_match_candidate_id",
                        column: x => x.match_candidate_id,
                        principalTable: "match_candidates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_match_evidence_source_event_id",
                        column: x => x.source_event_id,
                        principalTable: "source_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Deduplicated and versioned reasoning: name similarity, shared fixture, league, rival.");

            migrationBuilder.CreateTable(
                name: "source_event_participants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    participant_role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_event_participants", x => x.id);
                    table.UniqueConstraint("uq_event_role", x => new { x.source_event_id, x.participant_role });
                    table.ForeignKey(
                        name: "fk_source_event_participants_source_entity_id",
                        column: x => x.source_entity_id,
                        principalTable: "source_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_source_event_participants_source_event_id",
                        column: x => x.source_event_id,
                        principalTable: "source_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Home/away or general participants. Use team source entity, not a string.");

            migrationBuilder.CreateTable(
                name: "entity_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    source_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    master_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    confidence = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: true),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entity_links", x => x.id);
                    table.ForeignKey(
                        name: "fk_entity_links_decision_id",
                        column: x => x.decision_id,
                        principalTable: "match_decisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_entity_links_master_entity_id",
                        column: x => x.master_entity_id,
                        principalTable: "master_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_entity_links_source_entity_id",
                        column: x => x.source_entity_id,
                        principalTable: "source_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "One active master ID per source entity. Closed links remain for audit.");

            migrationBuilder.CreateIndex(
                name: "IX_entity_links_decision_id",
                table: "entity_links",
                column: "decision_id");

            migrationBuilder.CreateIndex(
                name: "ix_links_master_current",
                table: "entity_links",
                columns: new[] { "master_entity_id", "valid_to" });

            migrationBuilder.CreateIndex(
                name: "uq_entity_links_active",
                table: "entity_links",
                column: "source_entity_id",
                unique: true,
                filter: "valid_to IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_entity_relations_from_master_entity_id",
                table: "entity_relations",
                column: "from_master_entity_id");

            migrationBuilder.CreateIndex(
                name: "IX_entity_relations_to_master_entity_id",
                table: "entity_relations",
                column: "to_master_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_master_kind",
                table: "master_entities",
                columns: new[] { "entity_kind", "sport_code" });

            migrationBuilder.CreateIndex(
                name: "ix_candidate_source_status",
                table: "match_candidates",
                columns: new[] { "source_entity_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_match_candidates_candidate_master_entity_id",
                table: "match_candidates",
                column: "candidate_master_entity_id");

            migrationBuilder.CreateIndex(
                name: "IX_match_candidates_candidate_source_entity_id",
                table: "match_candidates",
                column: "candidate_source_entity_id");

            migrationBuilder.CreateIndex(
                name: "IX_match_decisions_master_entity_id",
                table: "match_decisions",
                column: "master_entity_id");

            migrationBuilder.CreateIndex(
                name: "IX_match_decisions_match_candidate_id",
                table: "match_decisions",
                column: "match_candidate_id");

            migrationBuilder.CreateIndex(
                name: "IX_match_decisions_source_entity_id",
                table: "match_decisions",
                column: "source_entity_id");

            migrationBuilder.CreateIndex(
                name: "IX_match_evidence_source_event_id",
                table: "match_evidence",
                column: "source_event_id");

            migrationBuilder.CreateIndex(
                name: "ix_job_due",
                table: "processing_jobs",
                columns: new[] { "status", "run_after" });

            migrationBuilder.CreateIndex(
                name: "ix_providers_connector",
                table: "provider_connections",
                column: "connector_key");

            migrationBuilder.CreateIndex(
                name: "ix_source_kind",
                table: "source_entities",
                columns: new[] { "entity_kind", "sport_code" });

            migrationBuilder.CreateIndex(
                name: "IX_source_event_participants_source_entity_id",
                table: "source_event_participants",
                column: "source_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_event_kickoff",
                table: "source_events",
                columns: new[] { "sport_code", "kickoff_at" });

            migrationBuilder.CreateIndex(
                name: "IX_source_events_competition_source_entity_id",
                table: "source_events",
                column: "competition_source_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_runs_feed_started",
                table: "sync_runs",
                columns: new[] { "source_feed_id", "started_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entity_links");

            migrationBuilder.DropTable(
                name: "entity_relations");

            migrationBuilder.DropTable(
                name: "master_names");

            migrationBuilder.DropTable(
                name: "match_evidence");

            migrationBuilder.DropTable(
                name: "processing_jobs");

            migrationBuilder.DropTable(
                name: "source_entity_names");

            migrationBuilder.DropTable(
                name: "source_event_participants");

            migrationBuilder.DropTable(
                name: "sync_cursors");

            migrationBuilder.DropTable(
                name: "sync_runs");

            migrationBuilder.DropTable(
                name: "match_decisions");

            migrationBuilder.DropTable(
                name: "source_events");

            migrationBuilder.DropTable(
                name: "match_candidates");

            migrationBuilder.DropTable(
                name: "master_entities");

            migrationBuilder.DropTable(
                name: "source_entities");

            migrationBuilder.DropTable(
                name: "source_feeds");

            migrationBuilder.DropTable(
                name: "provider_connections");
        }
    }
}
