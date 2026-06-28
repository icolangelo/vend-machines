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
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'PaymentTransactions'
          AND column_name = 'SendTelemetryToMachine'
          AND data_type <> 'boolean'
    ) THEN
        ALTER TABLE "PaymentTransactions"
        ALTER COLUMN "SendTelemetryToMachine" TYPE boolean
        USING "SendTelemetryToMachine" <> 0;
    ELSIF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'PaymentTransactions'
          AND column_name = 'SendTelemetryToMachine'
    ) THEN
        ALTER TABLE "PaymentTransactions"
        ADD COLUMN "SendTelemetryToMachine" boolean NOT NULL DEFAULT TRUE;
    END IF;
END $$;
""");
            }
            else
            {
                migrationBuilder.AddColumn<bool>(
                    name: "SendTelemetryToMachine",
                    table: "PaymentTransactions",
                    type: "INTEGER",
                    nullable: false,
                    defaultValue: true);
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
