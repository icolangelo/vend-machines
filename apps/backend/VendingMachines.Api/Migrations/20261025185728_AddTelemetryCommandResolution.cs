using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTelemetryCommandResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelemetryCommandResolutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TelemetryCommandId = table.Column<Guid>(type: "uuid", nullable: true),
                    TelemetryCommandDispatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    OperatorName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryCommandResolutions", x => x.Id);
                    table.CheckConstraint("CK_TelemetryCommandResolutions_OneCommand", "(\"TelemetryCommandId\" IS NOT NULL AND \"TelemetryCommandDispatchId\" IS NULL) OR (\"TelemetryCommandId\" IS NULL AND \"TelemetryCommandDispatchId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_TelemetryCommandResolutions_TelemetryCommands_TelemetryComm~",
                        column: x => x.TelemetryCommandId,
                        principalTable: "TelemetryCommands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryCommandResolutions_TelemetryCommandDispatchId",
                table: "TelemetryCommandResolutions",
                column: "TelemetryCommandDispatchId",
                unique: true,
                filter: "\"TelemetryCommandDispatchId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryCommandResolutions_TelemetryCommandId",
                table: "TelemetryCommandResolutions",
                column: "TelemetryCommandId",
                unique: true,
                filter: "\"TelemetryCommandId\" IS NOT NULL");

            migrationBuilder.Sql("""
                CREATE FUNCTION "ValidateTelemetryCommandResolutionTarget"()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    command_status text;
                    command_error text;
                    dispatch_status text;
                    dispatch_operation text;
                    dispatch_error text;
                BEGIN
                    IF length(btrim(NEW."OperatorName")) = 0 OR
                       length(btrim(NEW."Reason")) = 0 OR
                       length(btrim(NEW."EvidenceReference")) = 0 THEN
                        RAISE EXCEPTION 'telemetry_command_resolution_details_required' USING ERRCODE = 'P0001';
                    END IF;

                    IF NEW."TelemetryCommandId" IS NOT NULL THEN
                        SELECT command."Status", command."Error"
                        INTO command_status, command_error
                        FROM "TelemetryCommands" command
                        WHERE command."Id" = NEW."TelemetryCommandId";

                        IF NOT FOUND OR command_status IS DISTINCT FROM 'failed' OR
                           command_error IS NULL OR
                           command_error NOT IN ('ack_timeout', 'command_outcome_unknown') THEN
                            RAISE EXCEPTION 'telemetry_command_not_unresolved' USING ERRCODE = 'P0001';
                        END IF;
                    ELSE
                        SELECT dispatch."Status", dispatch."Operation", dispatch."Error"
                        INTO dispatch_status, dispatch_operation, dispatch_error
                        FROM "TelemetryCommandDispatches" dispatch
                        WHERE dispatch."Id" = NEW."TelemetryCommandDispatchId";

                        IF NOT FOUND OR dispatch_operation IS NULL OR dispatch_operation = 'MdbStatus' OR
                           (dispatch_status <> 'Indeterminate' AND
                            NOT (dispatch_status = 'Failed' AND dispatch_error IS NOT NULL AND
                                 dispatch_error IN ('ack_timeout', 'command_outcome_unknown')) AND
                            NOT (dispatch_status = 'Cancelled' AND dispatch_error = 'cancelled_before_dispatch')) THEN
                            RAISE EXCEPTION 'telemetry_command_not_unresolved' USING ERRCODE = 'P0001';
                        END IF;
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER "TR_TelemetryCommandResolutions_ValidateTarget"
                BEFORE INSERT ON "TelemetryCommandResolutions"
                FOR EACH ROW
                EXECUTE FUNCTION "ValidateTelemetryCommandResolutionTarget"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new InvalidOperationException(
                "TelemetryCommandResolutions contém reconciliações auditadas de comandos MDB; não reverta a migration nem apague as evidências físicas.");
        }
    }
}
