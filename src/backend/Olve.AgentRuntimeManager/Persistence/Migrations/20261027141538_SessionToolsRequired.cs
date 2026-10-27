using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Olve.AgentRuntimeManager.Persistence.Migrations
{
    /// <summary>
    /// A session always states its tools (M10, 2026-10-27): Claude Code's own tools can't be had any
    /// more, so sessions stored without (they ran on those) get all of ARM's tools.
    /// </summary>
    public partial class SessionToolsRequired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE Sessions SET Tools = '["arm_bash","arm_file_read","arm_file_edit","arm_file_create","arm_file_delete","arm_file_copy","arm_file_move"]'
                WHERE Tools IS NULL;
                """);
            migrationBuilder.AlterColumn<string>(
                name: "Tools",
                table: "Sessions",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Tools",
                table: "Sessions",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");
        }
    }
}
