using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stars.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UniqueProviderName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_provider_connections_name",
                table: "provider_connections",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_provider_connections_name",
                table: "provider_connections");
        }
    }
}
