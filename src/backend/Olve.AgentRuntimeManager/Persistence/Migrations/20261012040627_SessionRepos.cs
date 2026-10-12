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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Repos",
                table: "Sessions");
        }
    }
}
