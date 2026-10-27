using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Olve.AgentRuntimeManager.Persistence.Migrations
{
    /// <summary>
    /// Until 2026-10-27 a session's empty <c>tools</c> meant the provider's built-in tools; now it
    /// means no tools at all (M10). Sessions stored before keep what they got: unset, the built-ins.
    /// </summary>
    public partial class EmptyToolsWereBuiltIns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Sessions SET Tools = NULL WHERE Tools = '[]';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: unset meant the built-ins before too.
        }
    }
}
