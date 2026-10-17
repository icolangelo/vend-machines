using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentReconciliationTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    ALTER TABLE "PaymentTransactions"
                        ADD COLUMN IF NOT EXISTS "LastReconciliationAt" timestamp with time zone NULL;
                    """);
            }
            else
            {
                migrationBuilder.AddColumn<DateTime>(
                    name: "LastReconciliationAt",
                    table: "PaymentTransactions",
                    type: "timestamp with time zone",
                    nullable: true);
            }

            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_PaymentTransactions_Status_LastReconciliationAt_CreatedAt"
                    ON "PaymentTransactions" ("Status", "LastReconciliationAt", "CreatedAt");
                    """, suppressTransaction: true);

                migrationBuilder.Sql("""
                    DO $$
                    DECLARE index_is_valid boolean;
                    BEGIN
                        SELECT i.indisvalid AND i.indisready AND NOT i.indisunique
                               AND i.indnkeyatts = 3
                               AND pg_get_indexdef(i.indexrelid) LIKE '%("Status", "LastReconciliationAt", "CreatedAt")%'
                          INTO index_is_valid
                          FROM pg_index i
                          JOIN pg_class index_relation ON index_relation.oid = i.indexrelid
                          JOIN pg_namespace index_schema ON index_schema.oid = index_relation.relnamespace
                         WHERE index_relation.relname = 'IX_PaymentTransactions_Status_LastReconciliationAt_CreatedAt'
                           AND index_schema.nspname = current_schema();

                        IF COALESCE(index_is_valid, FALSE) IS NOT TRUE THEN
                            RAISE EXCEPTION 'O índice IX_PaymentTransactions_Status_LastReconciliationAt_CreatedAt está ausente ou inválido; remova-o com DROP INDEX CONCURRENTLY e repita a migration.';
                        END IF;
                    END $$;
                    """, suppressTransaction: true);
            }
            else
            {
                migrationBuilder.CreateIndex(
                    name: "IX_PaymentTransactions_Status_LastReconciliationAt_CreatedAt",
                    table: "PaymentTransactions",
                    columns: new[] { "Status", "LastReconciliationAt", "CreatedAt" });
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_Status_LastReconciliationAt_CreatedAt",
                table: "PaymentTransactions");

            migrationBuilder.DropColumn(
                name: "LastReconciliationAt",
                table: "PaymentTransactions");
        }
    }
}
