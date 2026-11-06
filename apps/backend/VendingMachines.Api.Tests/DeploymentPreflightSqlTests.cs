using Npgsql;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class DeploymentPreflightSqlTests
{
    [Fact]
    public async Task ReadOnlyPreflightCountsOpenSalesAndUnresolvedMachineCommands()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "O teste do preflight de release exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var schema = $"deployment_preflight_{Guid.NewGuid():N}";
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
                    "Id" integer PRIMARY KEY,
                    "State" text NOT NULL,
                    "StartedAt" timestamptz NOT NULL,
                    "ClosedAt" timestamptz NULL
                );
                CREATE TABLE "TelemetryCommandDispatches" (
                    "Id" integer PRIMARY KEY,
                    "Status" text NOT NULL,
                    "Operation" text NOT NULL,
                    "Error" text NULL
                );
                CREATE TABLE "TelemetryCommands" (
                    "Id" integer PRIMARY KEY,
                    "Status" text NOT NULL,
                    "Error" text NULL
                );
                CREATE TABLE "TelemetryCommandResolutions" (
                    "Id" integer PRIMARY KEY,
                    "TelemetryCommandId" integer NULL UNIQUE REFERENCES "TelemetryCommands"("Id"),
                    "TelemetryCommandDispatchId" integer NULL UNIQUE
                );
                CREATE TABLE "SystemSettings" (
                    "Id" integer PRIMARY KEY,
                    "ApplicationFeePercent" numeric(5,2) NOT NULL
                );
                CREATE TABLE "DeviceMessageInbox" (
                    "Id" integer PRIMARY KEY,
                    "Status" text NOT NULL
                );
                CREATE TABLE "WebhookInboxMessages" (
                    "Id" integer PRIMARY KEY,
                    "Status" text NOT NULL
                );
                CREATE TABLE "OutboxMessages" (
                    "Id" integer PRIMARY KEY,
                    "Status" text NOT NULL
                );
                CREATE TABLE "SessionAdmissionGates" (
                    "Id" integer PRIMARY KEY,
                    "NewSessionsPaused" boolean NOT NULL
                );
                INSERT INTO "SystemSettings" VALUES (1, 5.00);
                INSERT INTO "SessionAdmissionGates" VALUES (1, FALSE);
                INSERT INTO "MachineSessions" VALUES
                    (1, 'PaymentPending', now() - interval '12 seconds', NULL),
                    (2, 'Completed', now() - interval '1 minute', now());
                INSERT INTO "TelemetryCommandDispatches" VALUES
                    (1, 'Pending', 'Command', NULL),
                    (2, 'Processing', 'Command', NULL),
                    (3, 'Indeterminate', 'Command', NULL),
                    (4, 'Indeterminate', 'MdbStatus', NULL),
                    (5, 'Completed', 'Command', NULL),
                    (6, 'Failed', 'Command', NULL),
                    (7, 'Failed', 'Command', 'ack_timeout');
                INSERT INTO "TelemetryCommands" VALUES
                    (1, 'failed', 'ack_timeout'),
                    (2, 'failed', 'machine_offline'),
                    (3, 'acknowledged', NULL);
                INSERT INTO "DeviceMessageInbox" VALUES
                    (1, 'Pending'),
                    (2, 'Retry'),
                    (3, 'Processing'),
                    (4, 'Failed'),
                    (5, 'Processed');
                INSERT INTO "WebhookInboxMessages" VALUES
                    (1, 'Pending'),
                    (2, 'Retry'),
                    (3, 'Processing'),
                    (4, 'Failed'),
                    (5, 'Processed');
                INSERT INTO "OutboxMessages" VALUES
                    (1, 'pending'),
                    (2, 'processing'),
                    (3, 'failed'),
                    (4, 'processed');
                """, connection))
            {
                await setup.ExecuteNonQueryAsync();
            }

            var sql = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "TestData", "deploy-preflight.sql"));
            Assert.Equal(1, await ReadOpenSessionCountAsync(connection, sql));
            Assert.Equal(0, await ReadUnresolvedSessionClosureCountAsync(connection, sql));
            Assert.Equal(5, await ReadUnresolvedMachineCommandCountAsync(connection, sql));
            Assert.Equal(0, await ReadInvalidPlatformFeeSettingsCountAsync(connection, sql));
            Assert.Equal(1, await ReadOpenSessionAdmissionCountAsync(connection, sql));
            Assert.Equal(4, await ReadUnprocessedDeviceMessageCountAsync(connection, sql));
            Assert.Equal(4, await ReadUnprocessedPaymentWebhookCountAsync(connection, sql));
            Assert.Equal(1, await ReadFinancialOutboxInFlightCountAsync(connection, sql));

            await using (var pauseAdmission = new NpgsqlCommand(
                             "UPDATE \"SessionAdmissionGates\" SET \"NewSessionsPaused\" = TRUE WHERE \"Id\" = 1",
                             connection))
                Assert.Equal(1, await pauseAdmission.ExecuteNonQueryAsync());
            Assert.Equal(0, await ReadOpenSessionAdmissionCountAsync(connection, sql));

            await using (var invalidateFee = new NpgsqlCommand(
                             "UPDATE \"SystemSettings\" SET \"ApplicationFeePercent\" = 0 WHERE \"Id\" = 1",
                             connection))
                Assert.Equal(1, await invalidateFee.ExecuteNonQueryAsync());
            Assert.Equal(1, await ReadInvalidPlatformFeeSettingsCountAsync(connection, sql));

            await using (var invalidateFee = new NpgsqlCommand(
                             "UPDATE \"SystemSettings\" SET \"ApplicationFeePercent\" = 100 WHERE \"Id\" = 1",
                             connection))
                Assert.Equal(1, await invalidateFee.ExecuteNonQueryAsync());
            Assert.Equal(1, await ReadInvalidPlatformFeeSettingsCountAsync(connection, sql));

            await using (var restoreFee = new NpgsqlCommand(
                             "UPDATE \"SystemSettings\" SET \"ApplicationFeePercent\" = 7.25 WHERE \"Id\" = 1",
                             connection))
                Assert.Equal(1, await restoreFee.ExecuteNonQueryAsync());
            Assert.Equal(0, await ReadInvalidPlatformFeeSettingsCountAsync(connection, sql));

            await using (var closeSession = new NpgsqlCommand(
                             "UPDATE \"MachineSessions\" SET \"ClosedAt\" = now() WHERE \"Id\" = 1",
                             connection))
            {
                Assert.Equal(1, await closeSession.ExecuteNonQueryAsync());
            }

            Assert.Equal(0, await ReadOpenSessionCountAsync(connection, sql));

            await using (var markIncomplete = new NpgsqlCommand(
                             "UPDATE \"MachineSessions\" SET \"State\" = 'ClosureIncomplete' WHERE \"Id\" = 1",
                             connection))
                Assert.Equal(1, await markIncomplete.ExecuteNonQueryAsync());
            Assert.Equal(1, await ReadUnresolvedSessionClosureCountAsync(connection, sql));
            await using (var verifyClosure = new NpgsqlCommand(
                             "UPDATE \"MachineSessions\" SET \"State\" = 'ClosureVerified' WHERE \"Id\" = 1",
                             connection))
                Assert.Equal(1, await verifyClosure.ExecuteNonQueryAsync());
            Assert.Equal(0, await ReadUnresolvedSessionClosureCountAsync(connection, sql));

            await using (var resolveCommands = new NpgsqlCommand(
                             "UPDATE \"TelemetryCommandDispatches\" SET \"Status\" = 'Completed' WHERE \"Id\" IN (1, 2)",
                             connection))
            {
                Assert.Equal(2, await resolveCommands.ExecuteNonQueryAsync());
            }

            Assert.Equal(3, await ReadUnresolvedMachineCommandCountAsync(connection, sql));

            await using (var resolveLocalCommand = new NpgsqlCommand(
                             "INSERT INTO \"TelemetryCommandResolutions\" (\"Id\", \"TelemetryCommandId\", \"TelemetryCommandDispatchId\") VALUES (1, 1, NULL)",
                             connection))
                Assert.Equal(1, await resolveLocalCommand.ExecuteNonQueryAsync());
            Assert.Equal(2, await ReadUnresolvedMachineCommandCountAsync(connection, sql));

            await using (var resolveRemoteCommand = new NpgsqlCommand(
                             "INSERT INTO \"TelemetryCommandResolutions\" (\"Id\", \"TelemetryCommandId\", \"TelemetryCommandDispatchId\") VALUES (2, NULL, 3)",
                             connection))
                Assert.Equal(1, await resolveRemoteCommand.ExecuteNonQueryAsync());
            Assert.Equal(1, await ReadUnresolvedMachineCommandCountAsync(connection, sql));

            await using (var resolveFailedRemoteCommand = new NpgsqlCommand(
                             "INSERT INTO \"TelemetryCommandResolutions\" (\"Id\", \"TelemetryCommandId\", \"TelemetryCommandDispatchId\") VALUES (3, NULL, 7)",
                             connection))
                Assert.Equal(1, await resolveFailedRemoteCommand.ExecuteNonQueryAsync());
            Assert.Equal(0, await ReadUnresolvedMachineCommandCountAsync(connection, sql));
            await using (var processInbox = new NpgsqlCommand(
                             "UPDATE \"DeviceMessageInbox\" SET \"Status\" = 'Processed' WHERE \"Status\" <> 'Processed'",
                             connection))
                Assert.Equal(4, await processInbox.ExecuteNonQueryAsync());
            Assert.Equal(0, await ReadUnprocessedDeviceMessageCountAsync(connection, sql));

            await using (var processWebhooks = new NpgsqlCommand(
                             "UPDATE \"WebhookInboxMessages\" SET \"Status\" = 'Processed' WHERE \"Status\" <> 'Processed'",
                             connection))
                Assert.Equal(4, await processWebhooks.ExecuteNonQueryAsync());
            Assert.Equal(0, await ReadUnprocessedPaymentWebhookCountAsync(connection, sql));

            await using (var completeOutboxLease = new NpgsqlCommand(
                             "UPDATE \"OutboxMessages\" SET \"Status\" = 'processed' WHERE \"Status\" = 'processing'",
                             connection))
                Assert.Equal(1, await completeOutboxLease.ExecuteNonQueryAsync());
            Assert.Equal(0, await ReadFinancialOutboxInFlightCountAsync(connection, sql));
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(adminBuilder.ConnectionString);
            await cleanup.OpenAsync();
            await using var dropSchema = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", cleanup);
            await dropSchema.ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> ReadOpenSessionCountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount == 0 ||
                !string.Equals(reader.GetName(0), "active_vend_sessions", StringComparison.Ordinal))
                continue;

            Assert.Equal(3, reader.FieldCount);
            Assert.Equal("oldest_active_session_started_at_utc", reader.GetName(1));
            Assert.Equal("oldest_active_session_age_seconds", reader.GetName(2));
            Assert.True(await reader.ReadAsync());
            return reader.GetInt64(0);
        } while (await reader.NextResultAsync());

        throw new InvalidOperationException("O SQL de preflight não retornou a contagem de sessões abertas.");
    }

    private static async Task<long> ReadUnresolvedMachineCommandCountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount == 0 ||
                !string.Equals(reader.GetName(0), "unresolved_machine_commands", StringComparison.Ordinal))
                continue;

            Assert.Equal(1, reader.FieldCount);
            Assert.True(await reader.ReadAsync());
            return reader.GetInt64(0);
        } while (await reader.NextResultAsync());

        throw new InvalidOperationException("O SQL de preflight não retornou a contagem de comandos MDB não resolvidos.");
    }

    private static async Task<long> ReadUnresolvedSessionClosureCountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount == 0 ||
                !string.Equals(reader.GetName(0), "unresolved_session_closures", StringComparison.Ordinal))
                continue;

            Assert.Equal(1, reader.FieldCount);
            Assert.True(await reader.ReadAsync());
            return reader.GetInt64(0);
        } while (await reader.NextResultAsync());

        throw new InvalidOperationException("O SQL de preflight não retornou encerramentos físicos pendentes de confirmação.");
    }

    private static async Task<long> ReadInvalidPlatformFeeSettingsCountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount == 0 ||
                !string.Equals(reader.GetName(0), "invalid_platform_fee_settings", StringComparison.Ordinal))
                continue;

            Assert.Equal(1, reader.FieldCount);
            Assert.True(await reader.ReadAsync());
            return reader.GetInt64(0);
        } while (await reader.NextResultAsync());

        throw new InvalidOperationException("O SQL de preflight não retornou a contagem de taxas globais inválidas.");
    }

    private static async Task<long> ReadOpenSessionAdmissionCountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount == 0 ||
                !string.Equals(reader.GetName(0), "session_admission_open", StringComparison.Ordinal))
                continue;

            Assert.Equal(1, reader.FieldCount);
            Assert.True(await reader.ReadAsync());
            return reader.GetInt64(0);
        } while (await reader.NextResultAsync());

        throw new InvalidOperationException("O SQL de preflight não confirmou o bloqueio de novas sessões.");
    }

    private static async Task<long> ReadUnprocessedDeviceMessageCountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount == 0 ||
                !string.Equals(reader.GetName(0), "unprocessed_device_messages", StringComparison.Ordinal))
                continue;

            Assert.Equal(1, reader.FieldCount);
            Assert.True(await reader.ReadAsync());
            return reader.GetInt64(0);
        } while (await reader.NextResultAsync());

        throw new InvalidOperationException("O SQL de preflight não retornou a contagem de mensagens telemétricas não processadas.");
    }

    private static async Task<long> ReadUnprocessedPaymentWebhookCountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount == 0 ||
                !string.Equals(reader.GetName(0), "unprocessed_payment_webhooks", StringComparison.Ordinal))
                continue;

            Assert.Equal(1, reader.FieldCount);
            Assert.True(await reader.ReadAsync());
            return reader.GetInt64(0);
        } while (await reader.NextResultAsync());

        throw new InvalidOperationException("O SQL de preflight não retornou a contagem de webhooks de pagamento não processados.");
    }

    private static async Task<long> ReadFinancialOutboxInFlightCountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount == 0 ||
                !string.Equals(reader.GetName(0), "financial_outbox_in_flight", StringComparison.Ordinal))
                continue;

            Assert.Equal(1, reader.FieldCount);
            Assert.True(await reader.ReadAsync());
            return reader.GetInt64(0);
        } while (await reader.NextResultAsync());

        throw new InvalidOperationException("O SQL de preflight não retornou a contagem de operações financeiras em andamento.");
    }
}
