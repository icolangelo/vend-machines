using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class ClearLegacyTelemetryOwnerRoutingMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                        NEW."CommandRoutingVersion" := NULL;
                        NEW."OwnerLeaseExpiresAt" := NULL;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Reverter o reset de metadados apagaria a compatibilidade do roteamento MDB após reconexão legada.");
        }
    }
}
