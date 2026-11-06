using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using VendingMachines.Api.Data;

#nullable disable

namespace VendingMachines.Api.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260730120000_AddMdbStatusAndAutomaticSession")]
public partial class AddMdbStatusAndAutomaticSession : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "AutoOpenSessionEnabled",
            table: "Machines",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTime>(
            name: "AutoOpenLastAttemptAt",
            table: "MachineConnectionStates",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AutoOpenLastError",
            table: "MachineConnectionStates",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MdbStatus",
            table: "MachineConnectionStates",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MdbStatusRaw",
            table: "MachineConnectionStates",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "MdbStatusRequestAt",
            table: "MachineConnectionStates",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "MdbStatusRequestMessageId",
            table: "MachineConnectionStates",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "MdbStatusUpdatedAt",
            table: "MachineConnectionStates",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.Sql("""
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM "MachineSessions"
        WHERE "ClosedAt" IS NULL
        GROUP BY "MachineId"
        HAVING COUNT(*) > 1
    ) THEN
        RAISE EXCEPTION 'Existem máquinas com mais de uma sessão aberta. Corrija os dados antes de aplicar a migration.';
    END IF;
END $$;
""");

        migrationBuilder.DropIndex(
            name: "IX_MachineSessions_MachineId",
            table: "MachineSessions");

        migrationBuilder.CreateIndex(
            name: "UX_MachineSessions_OneOpenPerMachine",
            table: "MachineSessions",
            column: "MachineId",
            unique: true,
            filter: "\"ClosedAt\" IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_MachineSessions_OneOpenPerMachine",
            table: "MachineSessions");

        migrationBuilder.CreateIndex(
            name: "IX_MachineSessions_MachineId",
            table: "MachineSessions",
            column: "MachineId");

        migrationBuilder.DropColumn(
            name: "AutoOpenSessionEnabled",
            table: "Machines");

        migrationBuilder.DropColumn(
            name: "AutoOpenLastAttemptAt",
            table: "MachineConnectionStates");

        migrationBuilder.DropColumn(
            name: "AutoOpenLastError",
            table: "MachineConnectionStates");

        migrationBuilder.DropColumn(
            name: "MdbStatus",
            table: "MachineConnectionStates");

        migrationBuilder.DropColumn(
            name: "MdbStatusRaw",
            table: "MachineConnectionStates");

        migrationBuilder.DropColumn(
            name: "MdbStatusRequestAt",
            table: "MachineConnectionStates");

        migrationBuilder.DropColumn(
            name: "MdbStatusRequestMessageId",
            table: "MachineConnectionStates");

        migrationBuilder.DropColumn(
            name: "MdbStatusUpdatedAt",
            table: "MachineConnectionStates");
    }
}
