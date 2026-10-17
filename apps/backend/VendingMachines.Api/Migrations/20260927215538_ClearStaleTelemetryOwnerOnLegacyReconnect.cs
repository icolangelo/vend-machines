using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class ClearStaleTelemetryOwnerOnLegacyReconnect : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerConnectionId",
                table: "MachineConnectionStates",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION clear_legacy_telemetry_owner_on_connection_change()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF NEW."ConnectionId" IS DISTINCT FROM OLD."ConnectionId"
                       AND NEW."OwnerConnectionId" IS NOT DISTINCT FROM OLD."OwnerConnectionId" THEN
                        NEW."OwnerNodeId" := NULL;
                        NEW."OwnerConnectionId" := NULL;
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER "TR_MachineConnectionStates_ClearLegacyOwner"
                BEFORE UPDATE OF "ConnectionId" ON "MachineConnectionStates"
                FOR EACH ROW
                WHEN (OLD."ConnectionId" IS DISTINCT FROM NEW."ConnectionId")
                EXECUTE FUNCTION clear_legacy_telemetry_owner_on_connection_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_MachineConnectionStates_ClearLegacyOwner" ON "MachineConnectionStates";
                DROP FUNCTION IF EXISTS clear_legacy_telemetry_owner_on_connection_change();
                """);

            migrationBuilder.DropColumn(
                name: "OwnerConnectionId",
                table: "MachineConnectionStates");
        }
    }
}
