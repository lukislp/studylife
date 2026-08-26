using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuthInvites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UsedByUserId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthInvites", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthInvites_TokenHash",
                table: "AuthInvites",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthInvites");
        }
    }
}
