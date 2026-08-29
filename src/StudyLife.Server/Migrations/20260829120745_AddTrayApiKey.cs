using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddTrayApiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "TrayApiKeyCreatedAt",
                table: "AuthUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrayApiKeyHash",
                table: "AuthUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthUsers_TrayApiKeyHash",
                table: "AuthUsers",
                column: "TrayApiKeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuthUsers_TrayApiKeyHash",
                table: "AuthUsers");

            migrationBuilder.DropColumn(
                name: "TrayApiKeyCreatedAt",
                table: "AuthUsers");

            migrationBuilder.DropColumn(
                name: "TrayApiKeyHash",
                table: "AuthUsers");
        }
    }
}
