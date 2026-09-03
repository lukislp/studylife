using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddTelemetryConsentPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TelemetryConsent",
                table: "Settings",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TelemetryConsent",
                table: "Settings");
        }
    }
}
