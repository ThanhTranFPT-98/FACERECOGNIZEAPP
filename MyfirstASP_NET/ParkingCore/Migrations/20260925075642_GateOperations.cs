using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyfirstASP_NET.ParkingCore.Migrations
{
    /// <inheritdoc />
    public partial class GateOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Visits",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Visit");

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "Vehicles",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Registered");

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidUntilUtc",
                table: "Vehicles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewCategories",
                table: "SiteSettings",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "Delivery");

            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "SiteSettings",
                type: "TEXT",
                maxLength: 60,
                nullable: false,
                defaultValue: "Asia/Ho_Chi_Minh");

            migrationBuilder.AddColumn<int>(
                name: "WalkInResetHour",
                table: "SiteSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EntryPersonId",
                table: "Sessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ExitRequiresEntry",
                table: "Lanes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireInspectionPhoto",
                table: "Lanes",
                type: "INTEGER",
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
