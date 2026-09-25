using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyfirstASP_NET.ParkingCore.Migrations
{
    /// <inheritdoc />
    public partial class MonitoringAndAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SilentAlertSeconds",
                table: "Cameras",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WallSourceProtected",
                table: "Cameras",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AcknowledgedBy",
                table: "Alerts",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AcknowledgedUtc",
                table: "Alerts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolutionNote",
                table: "Alerts",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedUtc",
                table: "Alerts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Alerts",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Open");

            // Alerts closed before the lifecycle existed are Resolved, with the old resolver as acknowledger.
            migrationBuilder.Sql("UPDATE Alerts SET Status = 'Resolved', AcknowledgedBy = ResolvedBy WHERE Resolved = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SilentAlertSeconds",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "WallSourceProtected",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "AcknowledgedBy",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "AcknowledgedUtc",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "ResolutionNote",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "ResolvedUtc",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Alerts");
        }
    }
}
