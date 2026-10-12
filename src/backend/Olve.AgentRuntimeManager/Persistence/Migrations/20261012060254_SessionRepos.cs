using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Olve.AgentRuntimeManager.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionRepos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Repos",
                table: "Sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RunMessages",
                table: "Sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RunResume",
                table: "Sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorktreeRemovals",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Repos = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorktreeRemovals", x => x.SessionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorktreeRemovals_NextAttemptAt",
                table: "WorktreeRemovals",
                column: "NextAttemptAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorktreeRemovals");

            migrationBuilder.DropColumn(
                name: "Repos",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "RunMessages",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "RunResume",
                table: "Sessions");
        }
    }
}
