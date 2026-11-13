using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSAVehicle.ParkingCore.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class CameraRoiPolygonSqlServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RoiPolygon",
                table: "Cameras",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RoiPolygon",
                table: "Cameras");
        }
    }
}
