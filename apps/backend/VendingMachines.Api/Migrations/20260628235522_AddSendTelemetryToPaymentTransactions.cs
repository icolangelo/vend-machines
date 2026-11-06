using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSendTelemetryToPaymentTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'PaymentTransactions'
          AND column_name = 'SendTelemetryToMachine'
    ) THEN
        ALTER TABLE "PaymentTransactions"
        ADD COLUMN "SendTelemetryToMachine" integer NOT NULL DEFAULT 1;
    END IF;
END $$;
""");
            }
            else
            {
                migrationBuilder.AddColumn<int>(
                    name: "SendTelemetryToMachine",
                    table: "PaymentTransactions",
                    type: "INTEGER",
                    nullable: false,
                    defaultValue: 1);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SendTelemetryToMachine",
                table: "PaymentTransactions");
        }
    }
}
