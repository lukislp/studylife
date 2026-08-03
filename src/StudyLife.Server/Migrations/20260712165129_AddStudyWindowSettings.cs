using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddStudyWindowSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StudyDays",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "0,1,2,3,4,5,6");

            migrationBuilder.AddColumn<int>(
                name: "StudyWindowEndHour",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 21);

            migrationBuilder.AddColumn<int>(
                name: "StudyWindowStartHour",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 8);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StudyDays",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "StudyWindowEndHour",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "StudyWindowStartHour",
                table: "Settings");
        }
    }
}
