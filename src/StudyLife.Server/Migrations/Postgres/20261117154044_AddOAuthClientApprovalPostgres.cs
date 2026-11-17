using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddOAuthClientApprovalPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 2026-11-16 audit M5: registrations that already exist keep working - they are
            // grandfathered as Approved (1); every new row gets its state from
            // OAuthClientApprovalPolicy instead of this default.
            migrationBuilder.AddColumn<int>(
                name: "Approval",
                table: "OAuthClients",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Approval",
                table: "OAuthClients");
        }
    }
}
