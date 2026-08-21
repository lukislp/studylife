using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddCaptureApiKeyPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CaptureApiKeyCreatedAt",
                table: "AuthUsers",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CaptureApiKeyHash",
                table: "AuthUsers",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthUsers_CaptureApiKeyHash",
                table: "AuthUsers",
                column: "CaptureApiKeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuthUsers_CaptureApiKeyHash",
                table: "AuthUsers");

            migrationBuilder.DropColumn(
                name: "CaptureApiKeyCreatedAt",
                table: "AuthUsers");

            migrationBuilder.DropColumn(
                name: "CaptureApiKeyHash",
                table: "AuthUsers");
        }
    }
}
