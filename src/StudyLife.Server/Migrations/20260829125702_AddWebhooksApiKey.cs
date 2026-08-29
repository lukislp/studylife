using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhooksApiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "WebhooksApiKeyCreatedAt",
                table: "AuthUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhooksApiKeyHash",
                table: "AuthUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthUsers_WebhooksApiKeyHash",
                table: "AuthUsers",
                column: "WebhooksApiKeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuthUsers_WebhooksApiKeyHash",
                table: "AuthUsers");

            migrationBuilder.DropColumn(
                name: "WebhooksApiKeyCreatedAt",
                table: "AuthUsers");

            migrationBuilder.DropColumn(
                name: "WebhooksApiKeyHash",
                table: "AuthUsers");
        }
    }
}
