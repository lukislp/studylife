using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddAuthUserIsOwnerPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsOwner",
                table: "AuthUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Same backfill rationale as the SQLite twin (AddAuthUserIsOwner) - the lowest-Id
            // AuthUser becomes the persisted owner, making today's implicit rule explicit.
            migrationBuilder.Sql("""
                UPDATE "AuthUsers" SET "IsOwner" = TRUE WHERE "Id" = (SELECT "Id" FROM "AuthUsers" ORDER BY "Id" LIMIT 1);
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
