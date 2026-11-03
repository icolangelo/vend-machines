using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionAdmissionGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SessionAdmissionGates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NewSessionsPaused = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionAdmissionGates", x => x.Id);
                    table.CheckConstraint("CK_SessionAdmissionGates_Singleton", "\"Id\" = 1");
                });

            migrationBuilder.InsertData(
                table: "SessionAdmissionGates",
                columns: new[] { "Id", "NewSessionsPaused", "UpdatedAt", "UpdatedBy", "Reason" },
                values: new object[] { 1, false, new DateTime(2026, 10, 25, 18, 9, 55, DateTimeKind.Utc), "migration", "default" });

            migrationBuilder.Sql("""
                CREATE FUNCTION "RejectMachineSessionWhenDeploymentPaused"()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE admission_paused boolean;
                BEGIN
                    SELECT "NewSessionsPaused" INTO admission_paused
                    FROM "SessionAdmissionGates"
                    WHERE "Id" = 1
                    FOR SHARE;

                    IF admission_paused IS DISTINCT FROM FALSE THEN
                        RAISE EXCEPTION 'deployment_draining' USING ERRCODE = 'P0001';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER "TR_MachineSessions_RejectWhenDeploymentPaused"
                BEFORE INSERT ON "MachineSessions"
                FOR EACH ROW
                EXECUTE FUNCTION "RejectMachineSessionWhenDeploymentPaused"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new InvalidOperationException(
                "SessionAdmissionGates protege novas sessões durante releases; não reverta a migration nem remova o trigger. Corrija por migration para frente.");
        }
    }
}
