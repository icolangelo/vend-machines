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
        if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            migrationBuilder.Sql("""
                ALTER TABLE "Machines"
                    ADD COLUMN IF NOT EXISTS "AutoOpenSessionEnabled" boolean NOT NULL DEFAULT FALSE;
                ALTER TABLE "MachineConnectionStates"
                    ADD COLUMN IF NOT EXISTS "AutoOpenLastAttemptAt" timestamp with time zone NULL;
                ALTER TABLE "MachineConnectionStates"
                    ADD COLUMN IF NOT EXISTS "AutoOpenLastError" text NULL;
                ALTER TABLE "MachineConnectionStates"
                    ADD COLUMN IF NOT EXISTS "MdbStatus" text NULL;
                ALTER TABLE "MachineConnectionStates"
                    ADD COLUMN IF NOT EXISTS "MdbStatusRaw" text NULL;
                ALTER TABLE "MachineConnectionStates"
                    ADD COLUMN IF NOT EXISTS "MdbStatusRequestAt" timestamp with time zone NULL;
                ALTER TABLE "MachineConnectionStates"
                    ADD COLUMN IF NOT EXISTS "MdbStatusRequestMessageId" integer NULL;
                ALTER TABLE "MachineConnectionStates"
                    ADD COLUMN IF NOT EXISTS "MdbStatusUpdatedAt" timestamp with time zone NULL;
                """);
        }
        else
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
        }

        if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
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

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS "UX_MachineSessions_OneOpenPerMachine"
                ON "MachineSessions" ("MachineId")
                WHERE "ClosedAt" IS NULL;
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DO $$
                DECLARE index_is_valid boolean;
                BEGIN
                    SELECT i.indisvalid AND i.indisready AND i.indisunique
                           AND i.indnkeyatts = 1
                           AND pg_get_indexdef(i.indexrelid) LIKE '%USING btree ("MachineId") WHERE ("ClosedAt" IS NULL)'
                      INTO index_is_valid
                      FROM pg_index i
                      JOIN pg_class index_relation ON index_relation.oid = i.indexrelid
                      JOIN pg_namespace index_schema ON index_schema.oid = index_relation.relnamespace
                     WHERE index_relation.relname = 'UX_MachineSessions_OneOpenPerMachine'
                       AND index_schema.nspname = current_schema();

                    IF COALESCE(index_is_valid, FALSE) IS NOT TRUE THEN
                        RAISE EXCEPTION 'O índice UX_MachineSessions_OneOpenPerMachine está ausente ou inválido; remova-o com DROP INDEX CONCURRENTLY e repita a migration.';
                    END IF;
                END $$;
                """, suppressTransaction: true);

            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS \"IX_MachineSessions_MachineId\";",
                suppressTransaction: true);
        }
        else
        {
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
