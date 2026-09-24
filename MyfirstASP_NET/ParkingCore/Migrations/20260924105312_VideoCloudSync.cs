using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyfirstASP_NET.ParkingCore.Migrations
{
    /// <inheritdoc />
    public partial class VideoCloudSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Cameras",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LaneId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    SourceProtected = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Record24x7 = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSegmentUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cameras", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Outbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    EvidenceId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VideoSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CameraId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UploadedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ObjectKey = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoSegments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Status_Id",
                table: "Outbox",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_VideoSegments_CameraId_FileName",
                table: "VideoSegments",
                columns: new[] { "CameraId", "FileName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VideoSegments_CameraId_StartUtc",
                table: "VideoSegments",
                columns: new[] { "CameraId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_VideoSegments_Status_StartUtc",
                table: "VideoSegments",
                columns: new[] { "Status", "StartUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Cameras");

            migrationBuilder.DropTable(
                name: "Outbox");

            migrationBuilder.DropTable(
                name: "VideoSegments");
        }
    }
}
