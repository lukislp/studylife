using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddLinkTokenHashesPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuthUsers_CalendarToken",
                table: "AuthUsers");

            migrationBuilder.AddColumn<string>(
                name: "ProgressShareTokenHash",
                table: "Settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CalendarTokenHash",
                table: "AuthUsers",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Settings_ProgressShareTokenHash",
                table: "Settings",
                column: "ProgressShareTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_AuthUsers_CalendarTokenHash",
                table: "AuthUsers",
                column: "CalendarTokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Settings_ProgressShareTokenHash",
                table: "Settings");

            migrationBuilder.DropIndex(
                name: "IX_AuthUsers_CalendarTokenHash",
                table: "AuthUsers");

            migrationBuilder.DropColumn(
                name: "ProgressShareTokenHash",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "CalendarTokenHash",
                table: "AuthUsers");

            migrationBuilder.CreateIndex(
                name: "IX_AuthUsers_CalendarToken",
                table: "AuthUsers",
                column: "CalendarToken",
                unique: true);
        }
    }
}
