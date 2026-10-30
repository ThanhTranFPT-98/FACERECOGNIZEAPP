using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSAVehicle.ParkingCore.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class PpeTracksSqlServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PpeTrackId",
                table: "Events",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalRoi",
                table: "Cameras",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PpeTrackId",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "ExternalRoi",
                table: "Cameras");
        }
    }
}
