using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyfirstASP_NET.ParkingCore.Migrations
{
    /// <inheritdoc />
    public partial class CameraReceiver : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MergeWindowSeconds",
                table: "SiteSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<DateTime>(
                name: "CapturedUtc",
                table: "Events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReceivedUtc",
                table: "Events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AckUtc",
                table: "Commands",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalChannel",
                table: "Cameras",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalDevice",
                table: "Cameras",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastMessageUtc",
                table: "Cameras",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TrustedConfidence",
                table: "Cameras",
                type: "REAL",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CameraInbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Adapter = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RemoteIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PayloadSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AdapterVersion = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ProcessedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Observations = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CameraInbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceImages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TakenBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceImages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Cameras_ExternalDevice",
                table: "Cameras",
                column: "ExternalDevice");

            migrationBuilder.CreateIndex(
                name: "IX_CameraInbox_PayloadSha256",
                table: "CameraInbox",
                column: "PayloadSha256");

            migrationBuilder.CreateIndex(
                name: "IX_CameraInbox_Status_Id",
                table: "CameraInbox",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceImages_EventId",
                table: "EvidenceImages",
                column: "EventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CameraInbox");

            migrationBuilder.DropTable(
                name: "EvidenceImages");

            migrationBuilder.DropIndex(
                name: "IX_Cameras_ExternalDevice",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "MergeWindowSeconds",
                table: "SiteSettings");

            migrationBuilder.DropColumn(
                name: "CapturedUtc",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "ReceivedUtc",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "AckUtc",
                table: "Commands");

            migrationBuilder.DropColumn(
                name: "ExternalChannel",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "ExternalDevice",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "LastMessageUtc",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "TrustedConfidence",
                table: "Cameras");
        }
    }
}
