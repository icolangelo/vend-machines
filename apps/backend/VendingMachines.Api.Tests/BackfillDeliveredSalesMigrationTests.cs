using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using VendingMachines.Api.Data;
using VendingMachines.Api.Migrations;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class BackfillDeliveredSalesMigrationTests
{
    [Fact]
    public async Task MigrationBackfillsOnlyApprovedDeliveredSalesDeduplicatesEventsAndDownFailsClosedPreservingAllSales()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "O teste da migration de backfill de vendas exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var schema = $"sales_backfill_migration_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(configuredConnection);
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var createSchema = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin);
            await createSchema.ExecuteNonQueryAsync();
        }

        try
        {
            var scopedBuilder = new NpgsqlConnectionStringBuilder(configuredConnection) { SearchPath = schema };
            await using var connection = new NpgsqlConnection(scopedBuilder.ConnectionString);
            await connection.OpenAsync();
            await using (var setup = new NpgsqlCommand("""
                CREATE TABLE "MachineSessions" (
                    "Id" uuid PRIMARY KEY,
                    "CompanyId" uuid NOT NULL DEFAULT '10000000-0000-0000-0000-000000000001',
                    "MachineId" text NOT NULL DEFAULT 'M-1',
                    "TransactionId" uuid NULL,
                    "ItemNumber" integer NULL
                );
                CREATE TABLE "PaymentTransactions" (
                    "Id" uuid PRIMARY KEY,
                    "CompanyId" uuid NOT NULL,
                    "MachineId" text NOT NULL,
                    "Amount" numeric NOT NULL,
                    "ApplicationFee" numeric NOT NULL,
                    "Status" text NOT NULL
                );
                CREATE TABLE "TelemetryEvents" (
                    "Id" integer PRIMARY KEY,
                    "CompanyId" uuid NOT NULL DEFAULT '10000000-0000-0000-0000-000000000001',
                    "MachineId" text NOT NULL DEFAULT 'M-1',
                    "EventType" text NOT NULL,
                    "SessionId" uuid NULL,
                    "TransactionId" uuid NULL,
                    "CreatedAt" timestamptz NOT NULL
                );
                CREATE TABLE "SaleRecords" (
                    "Id" uuid PRIMARY KEY,
                    "CompanyId" uuid NOT NULL,
                    "MachineId" text NOT NULL,
                    "SessionId" uuid NOT NULL UNIQUE,
                    "TransactionId" uuid NOT NULL UNIQUE,
                    "ItemNumber" integer NOT NULL,
                    "ProductId" text NULL,
                    "ProductNameSnapshot" text NULL,
                    "Quantity" integer NOT NULL,
                    "GrossAmount" numeric NOT NULL,
                    "ApplicationFee" numeric NOT NULL,
                    "NetAmount" numeric NOT NULL,
                    "RefundedAmount" numeric NOT NULL DEFAULT 0,
                    "FinancialStatus" text NOT NULL DEFAULT 'Settled',
                    "SoldAt" timestamptz NOT NULL
                );

                INSERT INTO "PaymentTransactions" VALUES
                    ('20000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000001', 'M-1', 12.50, 0.63, 'Approved'),
                    ('20000000-0000-0000-0000-000000000002', '10000000-0000-0000-0000-000000000001', 'M-1', 8.00, 0.40, 'Approved'),
                    ('20000000-0000-0000-0000-000000000003', '10000000-0000-0000-0000-000000000001', 'M-1', 5.00, 0.25, 'Rejected'),
                    ('20000000-0000-0000-0000-000000000004', '10000000-0000-0000-0000-000000000001', 'M-1', 4.00, 0.20, 'Approved');
                INSERT INTO "MachineSessions" VALUES
                    ('00000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000001', 'M-1', '20000000-0000-0000-0000-000000000001', 1),
                    ('00000000-0000-0000-0000-000000000002', '10000000-0000-0000-0000-000000000001', 'M-1', '20000000-0000-0000-0000-000000000002', 2),
                    ('00000000-0000-0000-0000-000000000003', '10000000-0000-0000-0000-000000000001', 'M-1', '20000000-0000-0000-0000-000000000003', 3),
                    ('00000000-0000-0000-0000-000000000004', '10000000-0000-0000-0000-000000000001', 'M-1', '20000000-0000-0000-0000-000000000004', NULL),
                    ('00000000-0000-0000-0000-000000000005', '10000000-0000-0000-0000-000000000001', 'M-1', '20000000-0000-0000-0000-000000000001', 5);
                INSERT INTO "TelemetryEvents" ("Id", "EventType", "SessionId", "TransactionId", "CreatedAt") VALUES
                    (1, 'vend.success', '00000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000001', timestamptz '2026-01-01T10:00:00Z'),
                    (2, 'vend.success', '00000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000001', timestamptz '2026-01-01T10:00:05Z'),
                    (3, 'vend.success', '00000000-0000-0000-0000-000000000002', '20000000-0000-0000-0000-000000000002', timestamptz '2026-01-02T10:00:00Z'),
                    (4, 'vend.success', '00000000-0000-0000-0000-000000000003', '20000000-0000-0000-0000-000000000003', timestamptz '2026-01-03T10:00:00Z'),
                    (5, 'vend.success', '00000000-0000-0000-0000-000000000004', '20000000-0000-0000-0000-000000000004', timestamptz '2026-01-04T10:00:00Z'),
                    (6, 'vend.success', '00000000-0000-0000-0000-000000000005', '20000000-0000-0000-0000-000000000002', timestamptz '2026-01-05T10:00:00Z'),
                    (7, 'vend.failure', '00000000-0000-0000-0000-000000000002', '20000000-0000-0000-0000-000000000002', timestamptz '2026-01-06T10:00:00Z'),
                    (8, 'vend.success', NULL, '20000000-0000-0000-0000-000000000002', timestamptz '2026-01-07T10:00:00Z'),
                    (9, 'vend.success', '00000000-0000-0000-0000-000000000002', NULL, timestamptz '2026-01-08T10:00:00Z');
                """, connection))
            {
                await setup.ExecuteNonQueryAsync();
            }

            var migrationBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
            new ExposedBackfillDeliveredSalesMigration().ApplyUp(migrationBuilder);
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(scopedBuilder.ConnectionString)
                .Options;
            await using var db = new AppDbContext(options);
            var commands = db.GetService<IMigrationsSqlGenerator>()
                .Generate(migrationBuilder.Operations, model: null);
            await ExecuteMigrationCommandsAsync(connection, commands);

            var backfillBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
            new ExposedBackfillLegacyDeliveredSalesMigration().ApplyUp(backfillBuilder);
            commands = db.GetService<IMigrationsSqlGenerator>()
                .Generate(backfillBuilder.Operations, model: null);
            await ExecuteMigrationCommandsAsync(connection, commands);

            await using (var verify = new NpgsqlCommand("""
                SELECT "CompanyId", "MachineId", "SessionId", "TransactionId", "ItemNumber",
                       "ProductId", "ProductNameSnapshot", "Quantity", "GrossAmount",
                       "ApplicationFee", "NetAmount", "Origin", "SoldAt"
                FROM "SaleRecords"
                ORDER BY CASE WHEN "Origin" = 'LegacyBackfill' THEN 0 ELSE 1 END, "SessionId"
                """, connection))
            await using (var reader = await verify.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(Guid.Parse("10000000-0000-0000-0000-000000000001"), reader.GetGuid(0));
                Assert.Equal("M-1", reader.GetString(1));
                Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), reader.GetGuid(2));
                Assert.Equal(Guid.Parse("20000000-0000-0000-0000-000000000001"), reader.GetGuid(3));
                Assert.Equal(1, reader.GetInt32(4));
                Assert.True(await reader.IsDBNullAsync(5));
                Assert.True(await reader.IsDBNullAsync(6));
                Assert.Equal(1, reader.GetInt32(7));
                Assert.Equal(12.50m, reader.GetDecimal(8));
                Assert.Equal(0.63m, reader.GetDecimal(9));
                Assert.Equal(11.87m, reader.GetDecimal(10));
                Assert.Equal("LegacyBackfill", reader.GetString(11));
                Assert.Equal(DateTime.SpecifyKind(new DateTime(2026, 1, 1, 10, 0, 0), DateTimeKind.Utc), reader.GetDateTime(12));

                Assert.True(await reader.ReadAsync());
                Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000002"), reader.GetGuid(2));
                Assert.Equal(Guid.Parse("20000000-0000-0000-0000-000000000002"), reader.GetGuid(3));
                Assert.Equal(2, reader.GetInt32(4));
                Assert.Equal(8.00m, reader.GetDecimal(8));
                Assert.Equal(0.40m, reader.GetDecimal(9));
                Assert.Equal(7.60m, reader.GetDecimal(10));
                Assert.Equal("LegacyBackfill", reader.GetString(11));
                Assert.Equal(DateTime.SpecifyKind(new DateTime(2026, 1, 2, 10, 0, 0), DateTimeKind.Utc), reader.GetDateTime(12));
                Assert.False(await reader.ReadAsync());
            }

            await using (var insertCurrentSale = new NpgsqlCommand("""
                INSERT INTO "SaleRecords" (
                    "Id", "CompanyId", "MachineId", "SessionId", "TransactionId",
                    "ItemNumber", "Quantity", "GrossAmount", "ApplicationFee", "NetAmount", "SoldAt", "Origin"
                ) VALUES (
                    '30000000-0000-0000-0000-000000000001',
                    '10000000-0000-0000-0000-000000000001',
                    'M-1',
                    '00000000-0000-0000-0000-000000000006',
                    '20000000-0000-0000-0000-000000000005',
                    9, 1, 3.00, 0.15, 2.85,
                    timestamptz '2026-01-09T10:00:00Z',
                    'DeliveryConfirmed'
                )
                """, connection))
            {
                await insertCurrentSale.ExecuteNonQueryAsync();
            }

            var rollbackBackfillBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
            Assert.Throws<InvalidOperationException>(
                () => new ExposedBackfillLegacyDeliveredSalesMigration().ApplyDown(rollbackBackfillBuilder));

            await using (var verifyRollback = new NpgsqlCommand("""
                SELECT "SessionId", "TransactionId", "GrossAmount", "Origin"
                FROM "SaleRecords"
                ORDER BY "SessionId"
                """, connection))
            await using (var reader = await verifyRollback.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), reader.GetGuid(0));
                Assert.Equal(Guid.Parse("20000000-0000-0000-0000-000000000001"), reader.GetGuid(1));
                Assert.Equal(12.50m, reader.GetDecimal(2));
                Assert.Equal("LegacyBackfill", reader.GetString(3));
                Assert.True(await reader.ReadAsync());
                Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000002"), reader.GetGuid(0));
                Assert.Equal(Guid.Parse("20000000-0000-0000-0000-000000000002"), reader.GetGuid(1));
                Assert.Equal("LegacyBackfill", reader.GetString(3));
                Assert.True(await reader.ReadAsync());
                Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000006"), reader.GetGuid(0));
                Assert.Equal(Guid.Parse("20000000-0000-0000-0000-000000000005"), reader.GetGuid(1));
                Assert.Equal(3.00m, reader.GetDecimal(2));
                Assert.Equal("DeliveryConfirmed", reader.GetString(3));
                Assert.False(await reader.ReadAsync());
            }

            await using (var verifyColumn = new NpgsqlCommand("""
                SELECT count(*)
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND table_name = 'SaleRecords'
                  AND column_name = 'Origin'
                """, connection))
            {
                Assert.Equal(1L, (long)(await verifyColumn.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(adminBuilder.ConnectionString);
            await cleanup.OpenAsync();
            await using var dropSchema = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", cleanup);
            await dropSchema.ExecuteNonQueryAsync();
        }
    }

    private sealed class ExposedBackfillDeliveredSalesMigration : BackfillDeliveredSales
    {
        public void ApplyUp(MigrationBuilder migrationBuilder) => Up(migrationBuilder);
        public void ApplyDown(MigrationBuilder migrationBuilder) => Down(migrationBuilder);
    }

    private sealed class ExposedBackfillLegacyDeliveredSalesMigration : BackfillLegacyDeliveredSales
    {
        public void ApplyUp(MigrationBuilder migrationBuilder) => Up(migrationBuilder);
        public void ApplyDown(MigrationBuilder migrationBuilder) => Down(migrationBuilder);
    }

    private static async Task ExecuteMigrationCommandsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<MigrationCommand> commands)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var migrationCommand in commands)
        {
            await using var command = new NpgsqlCommand(migrationCommand.CommandText, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }
}
