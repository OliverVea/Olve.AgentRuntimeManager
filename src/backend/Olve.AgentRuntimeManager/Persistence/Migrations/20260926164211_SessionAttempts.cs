using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Olve.AgentRuntimeManager.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FailedAttempts",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Sessions from before retries were started at most once.
            migrationBuilder.Sql("UPDATE \"Sessions\" SET \"Attempts\" = 1 WHERE \"StartedAt\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "FailedAttempts",
                table: "Sessions");
        }
    }
}
