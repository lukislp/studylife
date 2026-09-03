using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddHotPathIndexesPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_StartTime",
                table: "Sessions");

            migrationBuilder.CreateIndex(
                name: "IX_StudyPrograms_AuthUserId",
                table: "StudyPrograms",
                column: "AuthUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SessionTemplates_AuthUserId",
                table: "SessionTemplates",
                column: "AuthUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_AuthUserId_StartTime",
                table: "Sessions",
                columns: new[] { "AuthUserId", "StartTime" });

            migrationBuilder.CreateIndex(
                name: "IX_Notes_AuthUserId_CourseId",
                table: "Notes",
                columns: new[] { "AuthUserId", "CourseId" });

            migrationBuilder.CreateIndex(
                name: "IX_Notes_AuthUserId_UpdatedAt",
                table: "Notes",
                columns: new[] { "AuthUserId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomCourses_AuthUserId",
                table: "CustomCourses",
                column: "AuthUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StudyPrograms_AuthUserId",
                table: "StudyPrograms");

            migrationBuilder.DropIndex(
                name: "IX_SessionTemplates_AuthUserId",
                table: "SessionTemplates");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_AuthUserId_StartTime",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_Notes_AuthUserId_CourseId",
                table: "Notes");

            migrationBuilder.DropIndex(
                name: "IX_Notes_AuthUserId_UpdatedAt",
                table: "Notes");

            migrationBuilder.DropIndex(
                name: "IX_CustomCourses_AuthUserId",
                table: "CustomCourses");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_StartTime",
                table: "Sessions",
                column: "StartTime");
        }
    }
}
