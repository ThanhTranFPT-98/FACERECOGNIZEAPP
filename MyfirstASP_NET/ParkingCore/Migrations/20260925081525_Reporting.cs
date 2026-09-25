using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyfirstASP_NET.ParkingCore.Migrations
{
    /// <inheritdoc />
    public partial class Reporting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SeedVersion",
                table: "Roles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "Shift",
                table: "Events",
                type: "TEXT",
                maxLength: 60,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_LaneId_CreatedUtc",
                table: "Events",
                columns: new[] { "LaneId", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Events_LaneId_CreatedUtc",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "SeedVersion",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "Shift",
                table: "Events");
        }
    }
}
