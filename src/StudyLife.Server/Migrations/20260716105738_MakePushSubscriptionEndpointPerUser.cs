using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class MakePushSubscriptionEndpointPerUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PushSubscriptions_Endpoint",
                table: "PushSubscriptions");

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_AuthUserId_Endpoint",
                table: "PushSubscriptions",
                columns: new[] { "AuthUserId", "Endpoint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PushSubscriptions_AuthUserId_Endpoint",
                table: "PushSubscriptions");

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_Endpoint",
                table: "PushSubscriptions",
                column: "Endpoint",
                unique: true);
        }
    }
}
