using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class RemovePasswordChangeRequiredPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChangeRequired",
                table: "PasswordCredentials");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ChangeRequired",
                table: "PasswordCredentials",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
