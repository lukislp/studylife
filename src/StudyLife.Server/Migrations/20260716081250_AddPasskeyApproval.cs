using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPasskeyApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAt",
                table: "PasskeyCredentials",
                type: "TEXT",
                nullable: true);

            // Backfill: retroactively approve all passkeys registered BEFORE this feature (phase 2
            // didn't have an approval concept yet, every passkey was usable immediately) - without this,
            // an already-registered device would suddenly be locked out after this update.
            // CreatedAt as the ApprovedAt value is a reasonable convention here (the exact timestamp
            // is unknown anyway); NEW additional passkeys from now on stay regularly NULL/pending.
            migrationBuilder.Sql("UPDATE PasskeyCredentials SET ApprovedAt = CreatedAt WHERE ApprovedAt IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "PasskeyCredentials");
        }
    }
}
