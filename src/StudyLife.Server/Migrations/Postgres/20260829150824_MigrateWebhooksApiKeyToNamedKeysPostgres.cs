using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace StudyLife.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class MigrateWebhooksApiKeyToNamedKeysPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebhookApiKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AuthUserId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    KeyHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookApiKeys", x => x.Id);
                });

            // Preserve any already-generated single Webhooks key as a named "Default" row
            // BEFORE the old column is dropped below - a user who already generated one keeps
            // it working instead of it silently disappearing. Double-quoted identifiers: Npgsql
            // migrations create PascalCase names, which Postgres would otherwise fold to
            // lowercase for an unquoted raw-SQL reference.
            migrationBuilder.Sql(@"
                INSERT INTO ""WebhookApiKeys"" (""AuthUserId"", ""Name"", ""KeyHash"", ""CreatedAt"")
                SELECT ""Id"", 'Default', ""WebhooksApiKeyHash"", COALESCE(""WebhooksApiKeyCreatedAt"", (NOW() AT TIME ZONE 'UTC'))
                FROM ""AuthUsers""
                WHERE ""WebhooksApiKeyHash"" IS NOT NULL;
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
    }
}
