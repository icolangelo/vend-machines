using Npgsql;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class PaymentSensitiveDataPreflightSqlTests
{
    [Fact]
    public async Task ReadOnlyPreflightAndBatchedRedactionHandleSyntheticSensitiveDataSafely()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "O teste do preflight de dados sensíveis exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var schema = $"payment_preflight_{Guid.NewGuid():N}";
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
                CREATE TABLE "PaymentTransactions" (
                    "Id" integer PRIMARY KEY,
                    "RawResponse" text NULL,
                    "CreatedAt" timestamptz NOT NULL
                );
                CREATE TABLE "TransactionTelemetryLogs" (
                    "Id" integer PRIMARY KEY,
                    "Message" text NOT NULL
                );
                CREATE TABLE "MercadoPagoIntegrations" (
                    "Id" integer PRIMARY KEY,
                    "LastTokenValidationStatus" text NOT NULL
                );
                INSERT INTO "PaymentTransactions"
                SELECT n, 'synthetic-payload', timestamptz '2026-01-01T00:00:00Z' + (n - 1) * interval '1 day'
                FROM generate_series(1, 502) AS series(n);
                INSERT INTO "TransactionTelemetryLogs"
                SELECT n, 'Erro retornado pelo Mercado Pago: synthetic diagnostic'
                FROM generate_series(1, 502) AS series(n);
                INSERT INTO "TransactionTelemetryLogs" VALUES (1000, 'mensagem sintética sem marcador sensível');
                INSERT INTO "MercadoPagoIntegrations"
                SELECT n, 'Falha ao renovar token OAuth: HTTP 503 sintetico'
                FROM generate_series(1, 502) AS series(n);
                INSERT INTO "MercadoPagoIntegrations" VALUES (1000, 'Token OAuth validado em /users/me.');
                """, connection))
            {
                await setup.ExecuteNonQueryAsync();
            }

            var sql = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "TestData", "payment-sensitive-data-preflight.sql"));

            await using var transaction = await connection.BeginTransactionAsync();
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                await readOnly.ExecuteNonQueryAsync();

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await using var reader = await command.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());
            Assert.Equal("transactions_with_raw_response", reader.GetName(0));
            Assert.Equal("raw_response_bytes", reader.GetName(1));
            Assert.Equal("oldest_transaction_with_raw_response", reader.GetName(2));
            Assert.Equal("newest_transaction_with_raw_response", reader.GetName(3));
            Assert.Equal(502L, reader.GetInt64(0));
            Assert.Equal((long)"synthetic-payload".Length * 502, reader.GetInt64(1));
            Assert.Equal(DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Utc), reader.GetDateTime(2));
            Assert.Equal(DateTime.SpecifyKind(new DateTime(2027, 5, 17), DateTimeKind.Utc), reader.GetDateTime(3));

            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal("legacy_sensitive_logs", reader.GetName(0));
            Assert.Equal("message_bytes", reader.GetName(1));
            Assert.Equal(502L, reader.GetInt64(0));
            Assert.Equal((long)System.Text.Encoding.UTF8.GetByteCount("Erro retornado pelo Mercado Pago: synthetic diagnostic") * 502, reader.GetInt64(1));

            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal("legacy_sensitive_token_statuses", reader.GetName(0));
            Assert.Equal("status_bytes", reader.GetName(1));
            Assert.Equal(502L, reader.GetInt64(0));
            Assert.Equal((long)System.Text.Encoding.UTF8.GetByteCount("Falha ao renovar token OAuth: HTTP 503 sintetico") * 502, reader.GetInt64(1));
            Assert.False(await reader.NextResultAsync());
            await reader.DisposeAsync();
            await transaction.RollbackAsync();

            var redactionSql = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "TestData", "payment-sensitive-data-redaction.sql"));
            await using (var redactFirstBatch = new NpgsqlCommand(redactionSql, connection))
                await redactFirstBatch.ExecuteNonQueryAsync();

            Assert.Equal((2L, 2L, 2L, 500L, 500L, 1L), await ReadRedactionStateAsync(connection));

            await using (var redactSecondBatch = new NpgsqlCommand(redactionSql, connection))
                await redactSecondBatch.ExecuteNonQueryAsync();

            Assert.Equal((0L, 0L, 0L, 502L, 502L, 1L), await ReadRedactionStateAsync(connection));

            await using (var rerun = new NpgsqlCommand(redactionSql, connection))
                await rerun.ExecuteNonQueryAsync();

            Assert.Equal((0L, 0L, 0L, 502L, 502L, 1L), await ReadRedactionStateAsync(connection));
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(adminBuilder.ConnectionString);
            await cleanup.OpenAsync();
            await using var dropSchema = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", cleanup);
            await dropSchema.ExecuteNonQueryAsync();
        }
    }

    private static async Task<(long RawResponses, long SensitiveLogs, long SensitiveStatuses, long RedactedLogs, long RedactedStatuses, long UnrelatedLogs)> ReadRedactionStateAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT COUNT(*) FROM "PaymentTransactions" WHERE "RawResponse" IS NOT NULL),
                (SELECT COUNT(*) FROM "TransactionTelemetryLogs" WHERE "Message" LIKE 'Erro retornado pelo Mercado Pago:%'),
                (SELECT COUNT(*) FROM "MercadoPagoIntegrations" WHERE "LastTokenValidationStatus" LIKE 'Falha ao renovar token OAuth:%'),
                (SELECT COUNT(*) FROM "TransactionTelemetryLogs" WHERE "Message" = 'Falha na integração de pagamento. Detalhes sensíveis foram removidos.'),
                (SELECT COUNT(*) FROM "MercadoPagoIntegrations" WHERE "LastTokenValidationStatus" = 'Falha na validação do token OAuth. Detalhes sensíveis foram removidos.'),
                (SELECT COUNT(*) FROM "TransactionTelemetryLogs" WHERE "Message" = 'mensagem sintética sem marcador sensível');
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
    }
}
