using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyfirstASP_NET.ParkingCore.Migrations
{
    /// <inheritdoc />
    public partial class PeopleAndPpe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PpeConfirmCount",
                table: "SiteSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "PpeWindowSeconds",
                table: "SiteSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<string>(
                name: "PpeMissingMode",
                table: "Lanes",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Review");

            migrationBuilder.AddColumn<string>(
                name: "PpeRules",
                table: "Lanes",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Subject",
                table: "Lanes",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Vehicle");

            migrationBuilder.AddColumn<string>(
                name: "PpeOutcome",
                table: "Events",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PpeSummary",
                table: "Events",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "People",
                columns: table => new
                {
                    PersonId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    ValidUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_People", x => x.PersonId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "People");

            migrationBuilder.DropColumn(
                name: "PpeConfirmCount",
                table: "SiteSettings");

            migrationBuilder.DropColumn(
                name: "PpeWindowSeconds",
                table: "SiteSettings");

            migrationBuilder.DropColumn(
                name: "PpeMissingMode",
                table: "Lanes");

            migrationBuilder.DropColumn(
                name: "PpeRules",
                table: "Lanes");

            migrationBuilder.DropColumn(
                name: "Subject",
                table: "Lanes");

            migrationBuilder.DropColumn(
                name: "PpeOutcome",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "PpeSummary",
                table: "Events");
        }
    }
}
