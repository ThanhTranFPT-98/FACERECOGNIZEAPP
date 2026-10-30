using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSAVehicle.ParkingCore.Migrations
{
    /// <inheritdoc />
    public partial class PpeTracks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PpeTrackId",
                table: "Events",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalRoi",
                table: "Cameras",
                type: "TEXT",
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
