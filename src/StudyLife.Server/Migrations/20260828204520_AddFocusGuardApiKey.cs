using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddFocusGuardApiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FocusGuardApiKeyCreatedAt",
                table: "AuthUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FocusGuardApiKeyHash",
                table: "AuthUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthUsers_FocusGuardApiKeyHash",
                table: "AuthUsers",
                column: "FocusGuardApiKeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuthUsers_FocusGuardApiKeyHash",
                table: "AuthUsers");

            migrationBuilder.DropColumn(
                name: "FocusGuardApiKeyCreatedAt",
                table: "AuthUsers");

            migrationBuilder.DropColumn(
                name: "FocusGuardApiKeyHash",
                table: "AuthUsers");
        }
    }
}
