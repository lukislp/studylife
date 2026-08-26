using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddPerUserUniqueRowsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Same dedup-before-unique-index rationale as the SQLite twin
            // (AddPerUserUniqueRows) - keep the highest Id (newest row) per AuthUserId.
            migrationBuilder.Sql("""
                DELETE FROM "TimerState" WHERE "Id" NOT IN (
                    SELECT MAX("Id") FROM "TimerState" GROUP BY "AuthUserId"
                );
                """);
            migrationBuilder.Sql("""
                DELETE FROM "Settings" WHERE "Id" NOT IN (
                    SELECT MAX("Id") FROM "Settings" GROUP BY "AuthUserId"
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_TimerState_AuthUserId",
                table: "TimerState",
                column: "AuthUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Settings_AuthUserId",
                table: "Settings",
                column: "AuthUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TimerState_AuthUserId",
                table: "TimerState");

            migrationBuilder.DropIndex(
                name: "IX_Settings_AuthUserId",
                table: "Settings");
        }
    }
}
