using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSAVehicle.ParkingCore.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class PublicBoardSqlServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicDisplays");

            migrationBuilder.AddColumn<string>(
                name: "BoardKeyProtected",
                table: "SiteSettings",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BoardKeyProtected",
                table: "SiteSettings");

            migrationBuilder.CreateTable(
                name: "PublicDisplays",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    AlarmHeader = table.Column<bool>(type: "bit", nullable: false),
                    CamerasJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Enlarge = table.Column<bool>(type: "bit", nullable: false),
                    HoldSeconds = table.Column<int>(type: "int", nullable: false),
                    KeyProtected = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LaneId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    LastSeenIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ShowWarn = table.Column<bool>(type: "bit", nullable: false),
                    Sound = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    StopText = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Subtitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Zoom = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicDisplays", x => x.Id);
                });
        }
    }
}
