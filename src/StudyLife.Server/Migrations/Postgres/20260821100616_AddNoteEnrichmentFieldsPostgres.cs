using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddNoteEnrichmentFieldsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EnrichedAt",
                table: "Notes",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "Notes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "Notes",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnrichedAt",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "Summary",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "Notes");
        }
    }
}
