using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class MigrateWebhooksApiKeyToNamedKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebhookApiKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AuthUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    KeyHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookApiKeys", x => x.Id);
                });

            // Preserve any already-generated single Webhooks key as a named "Default" row
            // BEFORE the old column is dropped below - a user who already generated one keeps
            // it working instead of it silently disappearing.
            migrationBuilder.Sql(@"
                INSERT INTO WebhookApiKeys (AuthUserId, Name, KeyHash, CreatedAt)
                SELECT Id, 'Default', WebhooksApiKeyHash, COALESCE(WebhooksApiKeyCreatedAt, CURRENT_TIMESTAMP)
                FROM AuthUsers
                WHERE WebhooksApiKeyHash IS NOT NULL;
            ");

            migrationBuilder.DropIndex(
                name: "IX_AuthUsers_WebhooksApiKeyHash",
                table: "AuthUsers");

            migrationBuilder.DropColumn(
                name: "WebhooksApiKeyCreatedAt",
                table: "AuthUsers");

            migrationBuilder.DropColumn(
                name: "WebhooksApiKeyHash",
                table: "AuthUsers");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookApiKeys_AuthUserId",
                table: "WebhookApiKeys",
                column: "AuthUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookApiKeys_KeyHash",
                table: "WebhookApiKeys",
                column: "KeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebhookApiKeys");

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
    }
}
