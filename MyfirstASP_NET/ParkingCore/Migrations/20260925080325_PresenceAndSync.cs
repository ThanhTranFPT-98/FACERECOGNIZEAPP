using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyfirstASP_NET.ParkingCore.Migrations
{
    /// <inheritdoc />
    public partial class PresenceAndSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EvidenceSha256",
                table: "Events",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OverstayMinutes",
                table: "Areas",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Presence",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StationId = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    AreaCode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SubjectKey = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    SubjectName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Direction = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    PassageUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LaneId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Shift = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Presence", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Shifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AreaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Start = table.Column<string>(type: "TEXT", maxLength: 5, nullable: false),
                    End = table.Column<string>(type: "TEXT", maxLength: 5, nullable: false)
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
                    Peer = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    LastId = table.Column<long>(type: "INTEGER", nullable: false),
                    LastKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
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
