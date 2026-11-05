using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSAVehicle.ParkingCore.Migrations
{
    /// <inheritdoc />
    public partial class PublicDisplays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublicDisplays",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LaneId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Subtitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    StopText = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CamerasJson = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Zoom = table.Column<double>(type: "REAL", nullable: false),
                    Enlarge = table.Column<bool>(type: "INTEGER", nullable: false),
                    AlarmHeader = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowWarn = table.Column<bool>(type: "INTEGER", nullable: false),
                    HoldSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Sound = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    KeyProtected = table.Column<string>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSeenIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
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
