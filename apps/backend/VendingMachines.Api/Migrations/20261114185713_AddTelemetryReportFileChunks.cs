using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTelemetryReportFileChunks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelemetryReportFileChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Part = table.Column<int>(type: "integer", nullable: false),
                    Data = table.Column<string>(type: "text", nullable: false),
                    ByteLength = table.Column<int>(type: "integer", nullable: false),
                    IsEndOfFile = table.Column<bool>(type: "boolean", nullable: false),
                    MessageId = table.Column<int>(type: "integer", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryReportFileChunks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TelemetryReportFileValidations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ValidatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PartCount = table.Column<int>(type: "integer", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    TransferStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsCrcValid = table.Column<bool>(type: "boolean", nullable: false),
                    CalculatedCrc = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    ReportedCrc = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    ValidationError = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryReportFileValidations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryReportFileChunks_CompanyId_MachineId_FileName_Part",
                table: "TelemetryReportFileChunks",
                columns: new[] { "CompanyId", "MachineId", "FileName", "Part" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryReportFileChunks_CompanyId_MachineId_ReceivedAt",
                table: "TelemetryReportFileChunks",
                columns: new[] { "CompanyId", "MachineId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryReportFileValidations_CompanyId_MachineId_FileName",
                table: "TelemetryReportFileValidations",
                columns: new[] { "CompanyId", "MachineId", "FileName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryReportFileValidations_CompanyId_MachineId_Validate~",
                table: "TelemetryReportFileValidations",
                columns: new[] { "CompanyId", "MachineId", "ValidatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new InvalidOperationException(
                "TelemetryReportFileChunks contém relatórios recebidos das máquinas; preserve os arquivos e reverta somente o binário.");
        }
    }
}
