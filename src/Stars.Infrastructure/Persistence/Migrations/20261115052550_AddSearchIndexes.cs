using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stars.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateIndex(
                name: "ix_source_entities_name_trgm",
                table: "source_entities",
                column: "normalized_name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_candidate_review",
                table: "match_candidates",
                columns: new[] { "status", "generated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_master_entities_name_trgm",
                table: "master_entities",
                column: "normalized_name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_source_entities_name_trgm",
                table: "source_entities");

            migrationBuilder.DropIndex(
                name: "ix_candidate_review",
                table: "match_candidates");

            migrationBuilder.DropIndex(
                name: "ix_master_entities_name_trgm",
                table: "master_entities");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");
        }
    }
}
