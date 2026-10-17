using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationalAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OperationalAlerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<string>(type: "text", nullable: true),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeduplicationKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Type = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperationalAlerts_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AlertReadReceipts",
                columns: table => new
                {
                    AlertId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertReadReceipts", x => new { x.AlertId, x.UserId });
                    table.ForeignKey(
                        name: "FK_AlertReadReceipts_OperationalAlerts_AlertId",
                        column: x => x.AlertId,
                        principalTable: "OperationalAlerts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AlertReadReceipts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertReadReceipts_UserId",
                table: "AlertReadReceipts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalAlerts_CompanyId_DeduplicationKey",
                table: "OperationalAlerts",
                columns: new[] { "CompanyId", "DeduplicationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationalAlerts_CompanyId_Status_UpdatedAt",
                table: "OperationalAlerts",
                columns: new[] { "CompanyId", "Status", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertReadReceipts");

            migrationBuilder.DropTable(
                name: "OperationalAlerts");
        }
    }
}
