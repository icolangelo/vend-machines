using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class BackfillDeliveredSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "SaleRecords",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "DeliveryConfirmed");

            migrationBuilder.Sql(
                """
                WITH legacy_sales AS (
                    SELECT DISTINCT ON (session."Id")
                        session."CompanyId",
                        session."MachineId",
                        session."Id" AS "SessionId",
                        tx."Id" AS "TransactionId",
                        session."ItemNumber",
                        tx."Amount" AS "GrossAmount",
                        tx."ApplicationFee",
                        tx."Amount" - tx."ApplicationFee" AS "NetAmount",
                        telemetry."CreatedAt" AS "SoldAt"
                    FROM "TelemetryEvents" AS telemetry
                    INNER JOIN "MachineSessions" AS session ON session."Id" = telemetry."SessionId"
                    INNER JOIN "PaymentTransactions" AS tx ON tx."Id" = telemetry."TransactionId"
                    WHERE telemetry."EventType" = 'vend.success'
                      AND telemetry."SessionId" IS NOT NULL
                      AND telemetry."TransactionId" IS NOT NULL
                      AND session."TransactionId" = tx."Id"
                      AND session."ItemNumber" IS NOT NULL
                      AND tx."Status" = 'Approved'
                    ORDER BY session."Id", telemetry."CreatedAt" ASC
                )
                INSERT INTO "SaleRecords" (
                    "Id", "CompanyId", "MachineId", "SessionId", "TransactionId",
                    "ItemNumber", "ProductId", "ProductNameSnapshot", "Quantity",
                    "GrossAmount", "ApplicationFee", "NetAmount", "Origin", "SoldAt"
                )
                SELECT md5("SessionId"::text || ':' || "TransactionId"::text)::uuid,
                       "CompanyId", "MachineId", "SessionId", "TransactionId",
                       "ItemNumber", NULL, NULL, 1, "GrossAmount", "ApplicationFee", "NetAmount",
                       'LegacyBackfill', "SoldAt"
                FROM legacy_sales
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM \"SaleRecords\" WHERE \"Origin\" = 'LegacyBackfill';");
            migrationBuilder.DropColumn(
                name: "Origin",
                table: "SaleRecords");
        }
    }
}
