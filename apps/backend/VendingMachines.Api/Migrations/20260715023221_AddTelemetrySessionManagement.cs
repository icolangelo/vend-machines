using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTelemetrySessionManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NormalizedSerialNumber",
                table: "Machines",
                type: "TEXT",
                nullable: true);

            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
DO $$
BEGIN
    IF EXISTS (
        SELECT upper(trim("SerialNumber"))
        FROM "Machines"
        WHERE "SerialNumber" IS NOT NULL AND trim("SerialNumber") <> ''
        GROUP BY upper(trim("SerialNumber"))
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'Existem números de série duplicados. Corrija-os antes de aplicar a migração.';
    END IF;
END $$;
UPDATE "Machines"
SET "NormalizedSerialNumber" = upper(trim("SerialNumber"))
WHERE "SerialNumber" IS NOT NULL AND trim("SerialNumber") <> '';
""");
            }
            else
            {
                migrationBuilder.Sql("""
UPDATE "Machines"
SET "NormalizedSerialNumber" = upper(trim("SerialNumber"))
WHERE "SerialNumber" IS NOT NULL AND trim("SerialNumber") <> '';
""");
            }

            migrationBuilder.CreateTable(
                name: "DeliveryFailures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalReason = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryFailures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MachineConnectionStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineId = table.Column<string>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MonitoringEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsOnline = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", nullable: true),
                    ConnectedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DisconnectedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ActivatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ActivatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeactivatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeactivatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineConnectionStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MachineConnectionStates_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MachineSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineId = table.Column<string>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CloseReason = table.Column<string>(type: "TEXT", nullable: true),
                    ItemNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    AmountCents = table.Column<int>(type: "INTEGER", nullable: true),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastEventAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SelectionDeadlineAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PaymentDeadlineAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeliveryDeadlineAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MachineSessions_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MachineSessions_PaymentTransactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "PaymentTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MessageType = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TelemetryCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MessageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    Command = table.Column<string>(type: "TEXT", nullable: false),
                    DataJson = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AckAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryCommands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TelemetryEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: true),
                    DataJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Machines_NormalizedSerialNumber",
                table: "Machines",
                column: "NormalizedSerialNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryFailures_TransactionId",
                table: "DeliveryFailures",
                column: "TransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MachineConnectionStates_CompanyId_IsOnline",
                table: "MachineConnectionStates",
                columns: new[] { "CompanyId", "IsOnline" });

            migrationBuilder.CreateIndex(
                name: "IX_MachineConnectionStates_MachineId",
                table: "MachineConnectionStates",
                column: "MachineId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MachineSessions_CompanyId_MachineId_State",
                table: "MachineSessions",
                columns: new[] { "CompanyId", "MachineId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_MachineSessions_MachineId",
                table: "MachineSessions",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_MachineSessions_TransactionId",
                table: "MachineSessions",
                column: "TransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Status_NextAttemptAt",
                table: "OutboxMessages",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryCommands_CompanyId_MachineId_CreatedAt",
                table: "TelemetryCommands",
                columns: new[] { "CompanyId", "MachineId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryEvents_CompanyId_MachineId_CreatedAt",
                table: "TelemetryEvents",
                columns: new[] { "CompanyId", "MachineId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeliveryFailures");

            migrationBuilder.DropTable(
                name: "MachineConnectionStates");

            migrationBuilder.DropTable(
                name: "MachineSessions");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "TelemetryCommands");

            migrationBuilder.DropTable(
                name: "TelemetryEvents");

            migrationBuilder.DropIndex(
                name: "IX_Machines_NormalizedSerialNumber",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "NormalizedSerialNumber",
                table: "Machines");
        }
    }
}
