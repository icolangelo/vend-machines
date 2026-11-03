using Npgsql;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class MercadoPagoStaticIntegrationPreflightTests
{
    [Fact]
    public async Task ReadOnlyPreflightCountsOnlyRowsTheCleanupMigrationDeletes()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "O teste do preflight de integração Mercado Pago exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var schema = $"mercadopago_static_preflight_{Guid.NewGuid():N}";
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
                CREATE TABLE "MercadoPagoIntegrations" (
                    "Id" integer PRIMARY KEY,
                    "ClientId" text NULL,
                    "IsActive" boolean NOT NULL
                );
                INSERT INTO "MercadoPagoIntegrations" VALUES
                    (1, NULL, TRUE),
                    (2, '', FALSE),
                    (3, 'application-id', TRUE),
                    (4, '   ', TRUE);
                """, connection))
            {
                await setup.ExecuteNonQueryAsync();
            }

            var sql = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "TestData", "mercadopago-static-integration-preflight.sql"));
            var resultFound = false;
            await using (var command = new NpgsqlCommand(sql, connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                do
                {
                    if (reader.FieldCount == 0 ||
                        !string.Equals(reader.GetName(0), "integrations_to_delete", StringComparison.Ordinal))
                        continue;

                    Assert.Equal(3, reader.FieldCount);
                    Assert.Equal("active_integrations_to_delete", reader.GetName(1));
                    Assert.Equal("inactive_integrations_to_delete", reader.GetName(2));
                    Assert.True(await reader.ReadAsync());
                    Assert.Equal(2L, reader.GetInt64(0));
                    Assert.Equal(1L, reader.GetInt64(1));
                    Assert.Equal(1L, reader.GetInt64(2));
                    resultFound = true;
                    break;
                } while (await reader.NextResultAsync());
            }

            Assert.True(resultFound, "O preflight não retornou as contagens das integrações legadas.");
            await using var verify = new NpgsqlCommand("SELECT count(*) FROM \"MercadoPagoIntegrations\"", connection);
            Assert.Equal(4L, (long)(await verify.ExecuteScalarAsync())!);
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
