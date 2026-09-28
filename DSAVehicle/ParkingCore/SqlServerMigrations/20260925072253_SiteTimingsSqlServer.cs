using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSAVehicle.ParkingCore.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class SiteTimingsSqlServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CooldownSeconds",
                table: "Lanes",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DecisionValidSeconds",
                table: "Lanes",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HeartbeatFreshSeconds",
                table: "Lanes",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OfflineAlertSeconds",
                table: "Lanes",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewWindowSeconds",
                table: "Lanes",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SiteSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    DecisionValidSeconds = table.Column<int>(type: "int", nullable: false),
                    ReviewWindowSeconds = table.Column<int>(type: "int", nullable: false),
                    CooldownSeconds = table.Column<int>(type: "int", nullable: false),
                    HeartbeatFreshSeconds = table.Column<int>(type: "int", nullable: false),
                    OfflineAlertSeconds = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SiteSettings");

            migrationBuilder.DropColumn(
                name: "CooldownSeconds",
                table: "Lanes");

            migrationBuilder.DropColumn(
                name: "DecisionValidSeconds",
                table: "Lanes");

            migrationBuilder.DropColumn(
                name: "HeartbeatFreshSeconds",
                table: "Lanes");

            migrationBuilder.DropColumn(
                name: "OfflineAlertSeconds",
                table: "Lanes");

            migrationBuilder.DropColumn(
                name: "ReviewWindowSeconds",
                table: "Lanes");
        }
    }
}
