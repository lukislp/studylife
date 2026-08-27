using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddReferentialIntegrityForeignKeysPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Same cleanup-before-constraint rationale as the SQLite twin
            // (AddReferentialIntegrityForeignKeys): Postgres validates existing rows against a new
            // FK by default at ADD CONSTRAINT time, so an orphan here would fail the migration
            // outright just like the SQLite table rebuild would. Note.SessionId/
            // TimerState.SessionId are the one relation with a REAL, expected orphan population in
            // production; the other five are expected no-ops but get the same defensive cleanup
            // anyway (unbrickable migration on any drifted DB). Required (NOT NULL) FK columns are
            // cleaned by deleting the orphan row itself (mirrors the CASCADE/RESTRICT semantics the
            // FK below enforces from now on); nullable FK columns are cleaned by nulling just the
            // dangling reference, matching the SET NULL behavior configured below.
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
        }
    }
}
