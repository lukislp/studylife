using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiTenantAuthUserFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SentReminders_Key",
                table: "SentReminders");

            migrationBuilder.DropIndex(
                name: "IX_CourseGoals_CourseId",
                table: "CourseGoals");

            migrationBuilder.AddColumn<int>(
                name: "AuthUserId",
                table: "TimerState",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AuthUserId",
                table: "StudyPrograms",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AuthUserId",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AuthUserId",
                table: "SessionTemplates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AuthUserId",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AuthUserId",
                table: "SentReminders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AuthUserId",
                table: "PushSubscriptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AuthUserId",
                table: "Notes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AuthUserId",
                table: "CustomCourses",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AuthUserId",
                table: "CourseResources",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AuthUserId",
                table: "CourseGroups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AuthUserId",
                table: "CourseGoals",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AuthUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthUsers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SentReminders_AuthUserId_Key",
                table: "SentReminders",
                columns: new[] { "AuthUserId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CourseGoals_AuthUserId_CourseId",
                table: "CourseGoals",
                columns: new[] { "AuthUserId", "CourseId" },
                unique: true);

            // Backfill (phase 1 of the multi-user rework): create exactly ONE AuthUser and assign ALL
            // existing rows to it - existing data stays exactly the same for today's single
            // user. The table is guaranteed to be empty at this point (it is
            // created directly above in this migration), so the subselect resolves unambiguously.
            // On a fresh DB (tests, new installation) the UPDATEs are no-ops, the one
            // user still exists afterward - exactly what the middleware and query filters expect.
            migrationBuilder.Sql("INSERT INTO AuthUsers (DisplayName, CreatedAt) VALUES ('Mein Studium', datetime('now'));");
            foreach (var table in new[]
                     {
                         "Sessions", "Settings", "PushSubscriptions", "SentReminders", "Notes",
                         "CourseGoals", "TimerState", "StudyPrograms", "CourseGroups",
                         "CustomCourses", "SessionTemplates", "CourseResources",
                     })
            {
                migrationBuilder.Sql($"UPDATE {table} SET AuthUserId = (SELECT Id FROM AuthUsers ORDER BY Id LIMIT 1);");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthUsers");

            migrationBuilder.DropIndex(
                name: "IX_SentReminders_AuthUserId_Key",
                table: "SentReminders");

            migrationBuilder.DropIndex(
                name: "IX_CourseGoals_AuthUserId_CourseId",
                table: "CourseGoals");

            migrationBuilder.DropColumn(
                name: "AuthUserId",
                table: "TimerState");

            migrationBuilder.DropColumn(
                name: "AuthUserId",
                table: "StudyPrograms");

            migrationBuilder.DropColumn(
                name: "AuthUserId",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "AuthUserId",
                table: "SessionTemplates");

            migrationBuilder.DropColumn(
                name: "AuthUserId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "AuthUserId",
                table: "SentReminders");

            migrationBuilder.DropColumn(
                name: "AuthUserId",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "AuthUserId",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "AuthUserId",
                table: "CustomCourses");

            migrationBuilder.DropColumn(
                name: "AuthUserId",
                table: "CourseResources");

            migrationBuilder.DropColumn(
                name: "AuthUserId",
                table: "CourseGroups");

            migrationBuilder.DropColumn(
                name: "AuthUserId",
                table: "CourseGoals");

            migrationBuilder.CreateIndex(
                name: "IX_SentReminders_Key",
                table: "SentReminders",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CourseGoals_CourseId",
                table: "CourseGoals",
                column: "CourseId",
                unique: true);
        }
    }
}
