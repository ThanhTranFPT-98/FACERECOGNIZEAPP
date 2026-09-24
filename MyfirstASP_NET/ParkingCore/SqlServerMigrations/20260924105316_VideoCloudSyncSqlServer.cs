using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyfirstASP_NET.ParkingCore.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class VideoCloudSyncSqlServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Cameras",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LaneId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SourceProtected = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Record24x7 = table.Column<bool>(type: "bit", nullable: false),
                    LastSegmentUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cameras", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Outbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EntityType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EvidenceId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SentUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VideoSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CameraId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UploadedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ObjectKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true)
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
