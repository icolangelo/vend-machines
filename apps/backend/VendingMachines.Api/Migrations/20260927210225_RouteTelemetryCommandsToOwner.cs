using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class RouteTelemetryCommandsToOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelemetryCommandDispatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<string>(type: "text", nullable: false),
                    OwnerNodeId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessingStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MessageId = table.Column<int>(type: "integer", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: true),
                    Error = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryCommandDispatches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryCommandDispatches_OwnerNodeId_Status_CreatedAt",
                table: "TelemetryCommandDispatches",
                columns: new[] { "OwnerNodeId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelemetryCommandDispatches");
        }
    }
}
