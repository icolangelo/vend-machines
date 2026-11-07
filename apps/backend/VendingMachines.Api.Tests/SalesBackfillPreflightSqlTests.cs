using Npgsql;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class SalesBackfillPreflightSqlTests
{
    [Fact]
    public async Task ReadOnlyPreflightPlansOnlyEligibleDeduplicatedSalesAndDoesNotMutateHistory()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "O teste do preflight de backfill de vendas exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var schema = $"sales_backfill_preflight_{Guid.NewGuid():N}";
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
                CREATE TABLE "TelemetryEvents" (
                    "Id" integer PRIMARY KEY,
                    "CompanyId" uuid NOT NULL DEFAULT '10000000-0000-0000-0000-000000000001',
                    "MachineId" text NOT NULL DEFAULT 'M-1',
                    "EventType" text NOT NULL,
                    "SessionId" uuid NULL,
                    "TransactionId" uuid NULL,
                    "CreatedAt" timestamptz NOT NULL
                );
                CREATE TABLE "MachineSessions" (
                    "Id" uuid PRIMARY KEY,
                    "CompanyId" uuid NOT NULL,
                    "MachineId" text NOT NULL,
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
                CREATE TABLE "SaleRecords" (
                    "Id" uuid PRIMARY KEY,
                    "CompanyId" uuid NOT NULL,
                    "MachineId" text NOT NULL,
                    "SessionId" uuid NOT NULL UNIQUE,
                    "TransactionId" uuid NOT NULL UNIQUE,
                    "ItemNumber" integer NOT NULL,
                    "Quantity" integer NOT NULL,
                    "GrossAmount" numeric NOT NULL,
                    "ApplicationFee" numeric NOT NULL,
                    "NetAmount" numeric NOT NULL
                );

                INSERT INTO "MachineSessions" VALUES
                    ('00000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000001', 'M-1', '20000000-0000-0000-0000-000000000001', 1),
                    ('00000000-0000-0000-0000-000000000002', '10000000-0000-0000-0000-000000000001', 'M-1', '20000000-0000-0000-0000-000000000002', 2),
                    ('00000000-0000-0000-0000-000000000003', '10000000-0000-0000-0000-000000000001', 'M-1', '20000000-0000-0000-0000-000000000003', 3),
                    ('00000000-0000-0000-0000-000000000004', '10000000-0000-0000-0000-000000000001', 'M-1', '20000000-0000-0000-0000-000000000004', NULL),
                    ('00000000-0000-0000-0000-000000000005', '10000000-0000-0000-0000-000000000001', 'M-1', '20000000-0000-0000-0000-000000000001', 5);
                INSERT INTO "PaymentTransactions" VALUES
                    ('20000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000001', 'M-1', 10, 1, 'Approved'),
                    ('20000000-0000-0000-0000-000000000002', '10000000-0000-0000-0000-000000000001', 'M-1', 10, 1, 'Approved'),
                    ('20000000-0000-0000-0000-000000000003', '10000000-0000-0000-0000-000000000001', 'M-1', 10, 1, 'Rejected'),
                    ('20000000-0000-0000-0000-000000000004', '10000000-0000-0000-0000-000000000001', 'M-1', 10, 1, 'Approved');
                INSERT INTO "TelemetryEvents" ("Id", "EventType", "SessionId", "TransactionId", "CreatedAt") VALUES
                    (1, 'vend.success', '00000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000001', now() - interval '2 seconds'),
                    (2, 'vend.success', '00000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000001', now() - interval '1 second'),
                    (3, 'vend.success', '00000000-0000-0000-0000-000000000002', '20000000-0000-0000-0000-000000000002', now()),
                    (4, 'vend.success', '00000000-0000-0000-0000-000000000003', '20000000-0000-0000-0000-000000000003', now()),
                    (5, 'vend.success', '00000000-0000-0000-0000-000000000004', '20000000-0000-0000-0000-000000000004', now()),
                    (6, 'vend.success', '00000000-0000-0000-0000-000000000005', '20000000-0000-0000-0000-000000000002', now()),
                    (7, 'vend.failure', '00000000-0000-0000-0000-000000000002', '20000000-0000-0000-0000-000000000002', now()),
                    (8, 'vend.success', NULL, '20000000-0000-0000-0000-000000000002', now()),
                    (9, 'vend.success', '00000000-0000-0000-0000-000000000002', NULL, now());
                ANALYZE "TelemetryEvents";
                ANALYZE "MachineSessions";
                ANALYZE "PaymentTransactions";
                ANALYZE "SaleRecords";
                """, connection))
            {
                await setup.ExecuteNonQueryAsync();
            }

            var sql = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "TestData", "sales-backfill-preflight.sql"));
            var uniquePlanFound = false;
            await using (var command = new NpgsqlCommand(sql, connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    if (reader.FieldCount == 1)
                    {
                        var line = reader.GetString(0);
                        if (line.Contains("Unique", StringComparison.Ordinal) &&
                            line.Contains("rows=2 loops=1", StringComparison.Ordinal))
                            uniquePlanFound = true;
                    }
                }

                while (await reader.NextResultAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        if (reader.FieldCount != 1) continue;
                        var line = reader.GetString(0);
                        if (line.Contains("Unique", StringComparison.Ordinal) &&
                            line.Contains("rows=2 loops=1", StringComparison.Ordinal))
                            uniquePlanFound = true;
                    }
                }
            }

            Assert.True(uniquePlanFound, "O preflight não confirmou duas vendas elegíveis após deduplicar eventos.");
            await using var verify = new NpgsqlCommand("SELECT count(*) FROM \"SaleRecords\"", connection);
            Assert.Equal(0L, (long)(await verify.ExecuteScalarAsync())!);
            await using var verifyEvents = new NpgsqlCommand("SELECT count(*) FROM \"TelemetryEvents\"", connection);
            Assert.Equal(9L, (long)(await verifyEvents.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(adminBuilder.ConnectionString);
            await cleanup.OpenAsync();
            await using var dropSchema = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", cleanup);
            await dropSchema.ExecuteNonQueryAsync();
        }
    }
}
