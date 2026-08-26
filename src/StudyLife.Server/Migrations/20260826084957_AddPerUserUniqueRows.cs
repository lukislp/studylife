using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPerUserUniqueRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Dedup before the unique indexes below, or CreateIndex fails outright on any DB
            // that already has duplicate rows for a user (multiple untracked get-or-create call
            // sites could race on a user's first write before this fix - see
            // EntityUpsertHelper/StudyLifeDb.OnModelCreating). Keep the highest Id per
            // AuthUserId - the newest row, since every write path re-saves the entire entity on
            // every PUT, so the newest row is the most likely to reflect the user's actual latest
            // settings/timer state.
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
