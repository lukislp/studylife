using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddTimeTrackAndFocusTunesApiKeysPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FocusTunesApiKeyCreatedAt",
                table: "AuthUsers",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FocusTunesApiKeyHash",
                table: "AuthUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TimeTrackApiKeyCreatedAt",
                table: "AuthUsers",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeTrackApiKeyHash",
                table: "AuthUsers",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthUsers_FocusTunesApiKeyHash",
                table: "AuthUsers",
                column: "FocusTunesApiKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthUsers_TimeTrackApiKeyHash",
                table: "AuthUsers",
                column: "TimeTrackApiKeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuthUsers_FocusTunesApiKeyHash",
                table: "AuthUsers");

            migrationBuilder.DropIndex(
                name: "IX_AuthUsers_TimeTrackApiKeyHash",
                table: "AuthUsers");

            migrationBuilder.DropColumn(
                name: "FocusTunesApiKeyCreatedAt",
                table: "AuthUsers");

            migrationBuilder.DropColumn(
                name: "FocusTunesApiKeyHash",
                table: "AuthUsers");

            migrationBuilder.DropColumn(
                name: "TimeTrackApiKeyCreatedAt",
                table: "AuthUsers");

            migrationBuilder.DropColumn(
                name: "TimeTrackApiKeyHash",
                table: "AuthUsers");
        }
    }
}
