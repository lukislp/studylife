using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthUserIsOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsOwner",
                table: "AuthUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Backfill (audit A15/A2 fix): make today's implicit "lowest Id" owner rule explicit
            // instead of re-derived at every check - the lowest-Id AuthUser (the legacy/first-
            // registered user, see AddMultiTenantAuthUserFoundation/AuthController.RegisterComplete)
            // becomes the persisted owner. No-op on an empty AuthUsers table (fresh install: the
            // very next registration sets IsOwner itself, see RegisterComplete).
            migrationBuilder.Sql("""
                UPDATE AuthUsers SET IsOwner = 1 WHERE Id = (SELECT Id FROM AuthUsers ORDER BY Id LIMIT 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsOwner",
                table: "AuthUsers");
        }
    }
}
