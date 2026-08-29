using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTimeTrackApiKey : Migration
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeTrackApiKeyHash",
                table: "AuthUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthUsers_TimeTrackApiKeyHash",
                table: "AuthUsers",
                column: "TimeTrackApiKeyHash",
                unique: true);
        }
    }
}
