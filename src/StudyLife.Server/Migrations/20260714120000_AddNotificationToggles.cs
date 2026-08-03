using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyLife.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationToggles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AchievementNotificationsEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "CourseGoalRemindersEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "InactivityRemindersEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "SessionRemindersEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "WeeklyReportEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AchievementNotificationsEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "CourseGoalRemindersEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "InactivityRemindersEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SessionRemindersEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "WeeklyReportEnabled",
                table: "Settings");
        }
    }
}
