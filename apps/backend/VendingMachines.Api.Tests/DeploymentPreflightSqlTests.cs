using Npgsql;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class DeploymentPreflightSqlTests
{
    [Fact]
    public async Task ReadOnlyPreflightCountsOpenPhysicalSessionsWithoutReturningIdentifiers()
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
                    "StartedAt" timestamptz NOT NULL,
                    "ClosedAt" timestamptz NULL
                );
                INSERT INTO "MachineSessions" VALUES
                    (1, now() - interval '12 seconds', NULL),
                    (2, now() - interval '1 minute', now());
                """, connection))
            {
                await setup.ExecuteNonQueryAsync();
            }

            var sql = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "TestData", "deploy-preflight.sql"));
            Assert.Equal(1, await ReadOpenSessionCountAsync(connection, sql));

            await using (var closeSession = new NpgsqlCommand(
                             "UPDATE \"MachineSessions\" SET \"ClosedAt\" = now() WHERE \"Id\" = 1",
                             connection))
            {
                Assert.Equal(1, await closeSession.ExecuteNonQueryAsync());
            }

            Assert.Equal(0, await ReadOpenSessionCountAsync(connection, sql));
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
}
