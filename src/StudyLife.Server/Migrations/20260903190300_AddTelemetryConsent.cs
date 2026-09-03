using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddTelemetryConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TelemetryConsent",
                table: "Settings",
                type: "INTEGER",
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
