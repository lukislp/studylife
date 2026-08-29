using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddWebhooksApiKeyPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "WebhooksApiKeyCreatedAt",
                table: "AuthUsers",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhooksApiKeyHash",
                table: "AuthUsers",
                type: "text",
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
