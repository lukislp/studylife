using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace StudyLife.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddTelemetryConsentLogPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelemetryConsentLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AuthUserId = table.Column<int>(type: "integer", nullable: false),
                    Answer = table.Column<bool>(type: "boolean", nullable: false),
                    AnsweredAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    TextVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryConsentLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryConsentLog_AuthUserId_AnsweredAt",
                table: "TelemetryConsentLog",
                columns: new[] { "AuthUserId", "AnsweredAt" });

            // The log starts with what the Settings rows already prove: every stored answer that has
            // its timestamp and text version (answers older than those columns cannot be proven
            // and stay out). From here on TelemetryConsentService appends each change.
            migrationBuilder.Sql(
                "INSERT INTO \"TelemetryConsentLog\" (\"AuthUserId\", \"Answer\", \"AnsweredAt\", \"TextVersion\") " +
                "SELECT \"AuthUserId\", \"TelemetryConsent\", \"TelemetryConsentAt\", \"TelemetryConsentVersion\" " +
                "FROM \"Settings\" " +
                "WHERE \"TelemetryConsent\" IS NOT NULL AND \"TelemetryConsentAt\" IS NOT NULL AND \"TelemetryConsentVersion\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelemetryConsentLog");
        }
    }
}
