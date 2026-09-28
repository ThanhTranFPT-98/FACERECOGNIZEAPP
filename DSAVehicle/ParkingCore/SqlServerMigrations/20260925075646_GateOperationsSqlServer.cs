using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSAVehicle.ParkingCore.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class GateOperationsSqlServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Visits",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Visit");

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "Vehicles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Registered");

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidUntilUtc",
                table: "Vehicles",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewCategories",
                table: "SiteSettings",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "Delivery");

            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "SiteSettings",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "Asia/Ho_Chi_Minh");

            migrationBuilder.AddColumn<int>(
                name: "WalkInResetHour",
                table: "SiteSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EntryPersonId",
                table: "Sessions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ExitRequiresEntry",
                table: "Lanes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireInspectionPhoto",
                table: "Lanes",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Visits");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "ValidUntilUtc",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "ReviewCategories",
                table: "SiteSettings");

            migrationBuilder.DropColumn(
                name: "TimeZone",
                table: "SiteSettings");

            migrationBuilder.DropColumn(
                name: "WalkInResetHour",
                table: "SiteSettings");

            migrationBuilder.DropColumn(
                name: "EntryPersonId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ExitRequiresEntry",
                table: "Lanes");

            migrationBuilder.DropColumn(
                name: "RequireInspectionPhoto",
                table: "Lanes");
        }
    }
}
