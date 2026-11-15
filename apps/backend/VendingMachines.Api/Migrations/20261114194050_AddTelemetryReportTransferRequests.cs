using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTelemetryReportTransferRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelemetryReportTransferRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    FileName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Error = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryReportTransferRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryReportTransferRequests_CompanyId_MachineId",
                table: "TelemetryReportTransferRequests",
                columns: new[] { "CompanyId", "MachineId" },
                unique: true,
                filter: "\"Status\" IN ('waiting_session', 'requesting', 'receiving')");

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryReportTransferRequests_CompanyId_MachineId_Created~",
                table: "TelemetryReportTransferRequests",
                columns: new[] { "CompanyId", "MachineId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelemetryReportTransferRequests");
        }
    }
}
