using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSAVehicle.ParkingCore.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class PublicDisplaysSqlServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublicDisplays",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LaneId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Subtitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StopText = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CamerasJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Zoom = table.Column<double>(type: "float", nullable: false),
                    Enlarge = table.Column<bool>(type: "bit", nullable: false),
                    AlarmHeader = table.Column<bool>(type: "bit", nullable: false),
                    ShowWarn = table.Column<bool>(type: "bit", nullable: false),
                    HoldSeconds = table.Column<int>(type: "int", nullable: false),
                    Sound = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    KeyProtected = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSeenIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicDisplays", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicDisplays");
        }
    }
}
