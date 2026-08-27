using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddReferentialIntegrityForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Cleanup BEFORE any of the FK constraints below are created, or the constraint
            // creation itself fails outright on any DB that already carries orphaned rows (for
            // SQLite specifically: the table-rebuild this migration performs re-inserts every row
            // through the new, FK-enforcing table before the rebuild's own temporary
            // "PRAGMA foreign_keys=0" window even opens - see the migration's generated SQL
            // script). Note.SessionId/TimerState.SessionId are the one relation with a REAL,
            // expected orphan population out in production (nothing ever cleaned a Note/TimerState
            // up when its Session was deleted) - the other five are expected no-ops here, but get
            // the same defensive cleanup anyway, so this migration can never crashloop a pod on a
            // drifted DB (see the k8s worker's WaitForPendingMigrationsAsync design in Program.cs).
            // Required (NOT NULL) FK columns are cleaned by deleting the orphan row itself
            // (mirrors the CASCADE/RESTRICT semantics the FK below enforces from now on - there is
            // no "unset" value for a required column); nullable FK columns are cleaned by nulling
            // just the dangling reference, matching the SET NULL behavior configured below.
            migrationBuilder.Sql("""
                DELETE FROM "CourseGroups" WHERE "StudyProgramId" NOT IN (
                    SELECT "Id" FROM "StudyPrograms"
                );
                """);
            migrationBuilder.Sql("""
                DELETE FROM "CustomCourses" WHERE "StudyProgramId" NOT IN (
                    SELECT "Id" FROM "StudyPrograms"
                );
                """);
            migrationBuilder.Sql("""
                UPDATE "CustomCourses" SET "CourseGroupId" = NULL
                WHERE "CourseGroupId" IS NOT NULL AND "CourseGroupId" NOT IN (
                    SELECT "Id" FROM "CourseGroups"
                );
                """);
            migrationBuilder.Sql("""
                DELETE FROM "AuthInvites" WHERE "CreatedByUserId" NOT IN (
                    SELECT "Id" FROM "AuthUsers"
                );
                """);
            migrationBuilder.Sql("""
                UPDATE "AuthInvites" SET "UsedByUserId" = NULL
                WHERE "UsedByUserId" IS NOT NULL AND "UsedByUserId" NOT IN (
                    SELECT "Id" FROM "AuthUsers"
                );
                """);
            migrationBuilder.Sql("""
                UPDATE "Notes" SET "SessionId" = NULL
                WHERE "SessionId" IS NOT NULL AND "SessionId" NOT IN (
                    SELECT "Id" FROM "Sessions"
                );
                """);
            migrationBuilder.Sql("""
                UPDATE "TimerState" SET "SessionId" = NULL
                WHERE "SessionId" IS NOT NULL AND "SessionId" NOT IN (
                    SELECT "Id" FROM "Sessions"
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_TimerState_SessionId",
                table: "TimerState",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Notes_SessionId",
                table: "Notes",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomCourses_CourseGroupId",
                table: "CustomCourses",
                column: "CourseGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthInvites_CreatedByUserId",
                table: "AuthInvites",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthInvites_UsedByUserId",
                table: "AuthInvites",
                column: "UsedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_AuthInvites_AuthUsers_CreatedByUserId",
                table: "AuthInvites",
                column: "CreatedByUserId",
                principalTable: "AuthUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AuthInvites_AuthUsers_UsedByUserId",
                table: "AuthInvites",
                column: "UsedByUserId",
                principalTable: "AuthUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_CourseGroups_StudyPrograms_StudyProgramId",
                table: "CourseGroups",
                column: "StudyProgramId",
                principalTable: "StudyPrograms",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomCourses_CourseGroups_CourseGroupId",
                table: "CustomCourses",
                column: "CourseGroupId",
                principalTable: "CourseGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomCourses_StudyPrograms_StudyProgramId",
                table: "CustomCourses",
                column: "StudyProgramId",
                principalTable: "StudyPrograms",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Notes_Sessions_SessionId",
                table: "Notes",
                column: "SessionId",
                principalTable: "Sessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_TimerState_Sessions_SessionId",
                table: "TimerState",
                column: "SessionId",
                principalTable: "Sessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Note on the Notes_fts_* triggers (migration AddNotesFts): the table rebuild this
            // migration performs on "Notes" (adding the FK above requires DROP + CREATE + copy +
            // rename on SQLite) drops those triggers along with the old table, same as it would
            // for any other schema change to Notes. They are deliberately NOT recreated inline
            // here - EF's SQLite generator defers the actual rebuild until after every raw Sql()
            // operation in this migration has already run at its literal position (a `dotnet ef
            // migrations script` dry run confirms this and even warns about it: "operation of
            // type SqlOperation will be attempted while a rebuild of table 'Notes' is pending"),
            // so a CREATE TRIGGER placed anywhere in THIS migration either collides with the
            // still-live old triggers (if it runs before the rebuild) or gets silently destroyed
            // by the rebuild's own DROP TABLE (if it runs after, within the same migration's
            // operation list). See the follow-up migration RestoreNotesFtsTriggersAfterFkRebuild,
            // which recreates them in a separate migration - by then the rebuild above is fully
            // committed, so the triggers stick.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuthInvites_AuthUsers_CreatedByUserId",
                table: "AuthInvites");

            migrationBuilder.DropForeignKey(
                name: "FK_AuthInvites_AuthUsers_UsedByUserId",
                table: "AuthInvites");

            migrationBuilder.DropForeignKey(
                name: "FK_CourseGroups_StudyPrograms_StudyProgramId",
                table: "CourseGroups");

            migrationBuilder.DropForeignKey(
                name: "FK_CustomCourses_CourseGroups_CourseGroupId",
                table: "CustomCourses");

            migrationBuilder.DropForeignKey(
                name: "FK_CustomCourses_StudyPrograms_StudyProgramId",
                table: "CustomCourses");

            migrationBuilder.DropForeignKey(
                name: "FK_Notes_Sessions_SessionId",
                table: "Notes");

            migrationBuilder.DropForeignKey(
                name: "FK_TimerState_Sessions_SessionId",
                table: "TimerState");

            migrationBuilder.DropIndex(
                name: "IX_TimerState_SessionId",
                table: "TimerState");

            migrationBuilder.DropIndex(
                name: "IX_Notes_SessionId",
                table: "Notes");

            migrationBuilder.DropIndex(
                name: "IX_CustomCourses_CourseGroupId",
                table: "CustomCourses");

            migrationBuilder.DropIndex(
                name: "IX_AuthInvites_CreatedByUserId",
                table: "AuthInvites");

            migrationBuilder.DropIndex(
                name: "IX_AuthInvites_UsedByUserId",
                table: "AuthInvites");

            // See the comment at the end of Up() - the Notes_fts_* triggers are restored by the
            // separate follow-up migration RestoreNotesFtsTriggersAfterFkRebuild, not here.
        }
    }
}
