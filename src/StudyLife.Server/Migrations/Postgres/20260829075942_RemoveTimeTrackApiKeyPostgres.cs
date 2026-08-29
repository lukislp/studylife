using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class RemoveTimeTrackApiKeyPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuthUsers_TimeTrackApiKeyHash",
                table: "AuthUsers");

            migrationBuilder.DropColumn(
                name: "TimeTrackApiKeyCreatedAt",
                table: "AuthUsers");

            migrationBuilder.DropColumn(
                name: "TimeTrackApiKeyHash",
                table: "AuthUsers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
                name: "IX_AuthUsers_TimeTrackApiKeyHash",
                table: "AuthUsers",
                column: "TimeTrackApiKeyHash",
                unique: true);
        }
    }
}
