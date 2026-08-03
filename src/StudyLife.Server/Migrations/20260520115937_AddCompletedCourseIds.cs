using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddCompletedCourseIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CompletedCourseIds",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite doesn't support DROP COLUMN in older versions;
            // recreating the table is the safe rollback path.
            migrationBuilder.Sql("""
                CREATE TABLE "Settings_backup" AS SELECT
                    "Id", "SelectedCourseIds", "Theme",
                    "AutoSwitchFocus", "AutoSwitchMinutesBefore", "MotivationalStyle"
                FROM "Settings";
                DROP TABLE "Settings";
                ALTER TABLE "Settings_backup" RENAME TO "Settings";
                """);
        }
    }
}
