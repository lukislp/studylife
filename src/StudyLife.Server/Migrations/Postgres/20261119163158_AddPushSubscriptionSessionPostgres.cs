using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddPushSubscriptionSessionPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuthSessionId",
                table: "PushSubscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_AuthSessionId",
                table: "PushSubscriptions",
                column: "AuthSessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PushSubscriptions_AuthSessionId",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "AuthSessionId",
                table: "PushSubscriptions");
        }
    }
}
