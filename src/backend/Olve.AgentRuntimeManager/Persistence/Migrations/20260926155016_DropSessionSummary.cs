using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Olve.AgentRuntimeManager.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropSessionSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Summary",
                table: "Sessions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "Sessions",
                type: "TEXT",
                nullable: true);
        }
    }
}
