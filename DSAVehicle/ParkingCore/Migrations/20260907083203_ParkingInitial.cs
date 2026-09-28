using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DSAVehicle.ParkingCore.Migrations
{
    /// <inheritdoc />
    public partial class ParkingInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    EventId = table.Column<Guid>(nullable: true),
                    Reason = table.Column<string>(nullable: false),
                    Severity = table.Column<string>(nullable: false),
                    Resolved = table.Column<bool>(nullable: false),
                    ResolvedBy = table.Column<string>(nullable: true),
                    CreatedUtc = table.Column<DateTime>(nullable: false),
                    DeliveryAttempts = table.Column<int>(nullable: false),
                    NextDeliveryUtc = table.Column<DateTime>(nullable: true),
                    DeliveredUtc = table.Column<DateTime>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Audit",
                columns: table => new
                {
                    Id = table.Column<long>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatedUtc = table.Column<DateTime>(nullable: false),
                    Actor = table.Column<string>(nullable: false),
                    Action = table.Column<string>(nullable: false),
                    Target = table.Column<string>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Audit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Commands",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    EventId = table.Column<Guid>(nullable: false),
                    LaneId = table.Column<string>(nullable: false),
                    Status = table.Column<string>(nullable: false),
                    CreatedUtc = table.Column<DateTime>(nullable: false),
                    PassageUtc = table.Column<DateTime>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Commands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    CorrelationId = table.Column<string>(maxLength: 100, nullable: false),
                    LaneId = table.Column<string>(nullable: false),
                    Plate = table.Column<string>(nullable: false),
                    PlateConfidence = table.Column<double>(nullable: false),
                    PersonId = table.Column<int>(nullable: true),
                    FaceConfidence = table.Column<double>(nullable: false),
                    ModelVersion = table.Column<string>(nullable: false),
                    Outcome = table.Column<string>(nullable: false),
                    Reason = table.Column<string>(nullable: false),
                    Source = table.Column<string>(nullable: false),
                    VisitId = table.Column<Guid>(nullable: true),
                    EvidenceId = table.Column<string>(nullable: true),
                    CreatedUtc = table.Column<DateTime>(nullable: false),
                    ExpiresUtc = table.Column<DateTime>(nullable: false),
                    ReviewBy = table.Column<string>(nullable: true),
                    ReviewNote = table.Column<string>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Lanes",
                columns: table => new
                {
                    Id = table.Column<string>(maxLength: 40, nullable: false),
                    Direction = table.Column<string>(nullable: false),
                    Policy = table.Column<string>(nullable: false),
                    PlateThreshold = table.Column<double>(nullable: false),
                    FaceThreshold = table.Column<double>(nullable: false),
                    ShadowMode = table.Column<bool>(nullable: false),
                    Simulation = table.Column<bool>(nullable: false),
                    LastHeartbeatUtc = table.Column<DateTime>(nullable: true),
                    SafetyHealthy = table.Column<bool>(nullable: false),
                    VehiclePresent = table.Column<bool>(nullable: false),
                    Obstructed = table.Column<bool>(nullable: false),
                    BarrierState = table.Column<string>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lanes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Username = table.Column<string>(maxLength: 100, nullable: false),
                    PasswordHash = table.Column<string>(nullable: false),
                    Role = table.Column<string>(nullable: false),
                    Active = table.Column<bool>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Vehicles",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Plate = table.Column<string>(nullable: false),
                    NormalizedPlate = table.Column<string>(maxLength: 32, nullable: false),
                    Kind = table.Column<string>(nullable: false),
                    Owner = table.Column<string>(nullable: false),
                    PersonId = table.Column<int>(nullable: true),
                    Active = table.Column<bool>(nullable: false),
                    Blocked = table.Column<bool>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vehicles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Visits",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(nullable: false),
                    Host = table.Column<string>(nullable: false),
                    Plate = table.Column<string>(nullable: false),
                    FromUtc = table.Column<DateTime>(nullable: false),
                    UntilUtc = table.Column<DateTime>(nullable: false),
                    MaxEntries = table.Column<int>(nullable: false),
                    Entries = table.Column<int>(nullable: false),
                    Revoked = table.Column<bool>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Visits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_CreatedUtc",
                table: "Alerts",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Commands_EventId",
                table: "Commands",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_CorrelationId",
                table: "Events",
                column: "CorrelationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_CreatedUtc",
                table: "Events",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_NormalizedPlate",
                table: "Vehicles",
                column: "NormalizedPlate",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "Audit");

            migrationBuilder.DropTable(
                name: "Commands");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "Lanes");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Vehicles");

            migrationBuilder.DropTable(
                name: "Visits");
        }
    }
}
