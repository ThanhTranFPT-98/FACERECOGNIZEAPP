using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyfirstASP_NET.ParkingCore.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class PresenceAndSyncSqlServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EvidenceSha256",
                table: "Events",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OverstayMinutes",
                table: "Areas",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Presence",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StationId = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    AreaCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SubjectKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    SubjectName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    PassageUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LaneId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Shift = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Presence", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Shifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AreaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Start = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    End = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shifts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Shifts_Areas_AreaId",
                        column: x => x.AreaId,
                        principalTable: "Areas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncCursors",
                columns: table => new
                {
                    Peer = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    LastId = table.Column<long>(type: "bigint", nullable: false),
                    LastKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncCursors", x => x.Peer);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Presence_AreaCode_PassageUtc",
                table: "Presence",
                columns: new[] { "AreaCode", "PassageUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Presence_EventId",
                table: "Presence",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Presence_StationId_Id",
                table: "Presence",
                columns: new[] { "StationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Shifts_AreaId",
                table: "Shifts",
                column: "AreaId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Presence");

            migrationBuilder.DropTable(
                name: "Shifts");

            migrationBuilder.DropTable(
                name: "SyncCursors");

            migrationBuilder.DropColumn(
                name: "EvidenceSha256",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "OverstayMinutes",
                table: "Areas");
        }
    }
}
