using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPushChannelApns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApnsToken",
                table: "PushSubscriptions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Channel",
                table: "PushSubscriptions",
                type: "TEXT",
                nullable: false,
                defaultValue: "webpush");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApnsToken",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "PushSubscriptions");
        }
    }
}
