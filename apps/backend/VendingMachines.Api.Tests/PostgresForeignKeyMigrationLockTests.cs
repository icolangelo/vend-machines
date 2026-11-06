using Npgsql;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class PostgresForeignKeyMigrationLockTests
{
    [Fact]
    public async Task CreateTableForeignKeyHonorsLockTimeoutAndCanBeRetriedAfterTheLockIsReleased()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "O teste de lock de FK exige VENDMACHINES_TEST_POSTGRES no CI.");
            }

            return;
        }

        var schema = $"migration_fk_lock_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Timeout = 5,
            CommandTimeout = 5
        };

        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var createSchema = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin);
            await createSchema.ExecuteNonQueryAsync();
        }

        try
        {
            var scopedBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
            {
                SearchPath = schema,
                Timeout = 5,
                CommandTimeout = 5
            };

            await using (var setup = new NpgsqlConnection(scopedBuilder.ConnectionString))
            {
                await setup.OpenAsync();
                await using var createParent = new NpgsqlCommand(
                    "CREATE TABLE \"Companies\" (\"Id\" integer PRIMARY KEY)",
                    setup);
                await createParent.ExecuteNonQueryAsync();
            }

            await using (var blockerConnection = new NpgsqlConnection(scopedBuilder.ConnectionString))
            {
                await blockerConnection.OpenAsync();
                await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
                await using (var holdWriteLock = new NpgsqlCommand(
                                 "LOCK TABLE \"Companies\" IN ROW EXCLUSIVE MODE",
                                 blockerConnection,
                                 blockerTransaction))
                {
                    await holdWriteLock.ExecuteNonQueryAsync();
                }

                await using var migrationConnection = new NpgsqlConnection(scopedBuilder.ConnectionString);
                await migrationConnection.OpenAsync();
                await using (var setTimeout = new NpgsqlCommand("SET lock_timeout = '250ms'", migrationConnection))
                {
                    await setTimeout.ExecuteNonQueryAsync();
                }

                await using (var createChild = new NpgsqlCommand("""
                                 CREATE TABLE "CompanyReferences" (
                                     "Id" integer PRIMARY KEY,
                                     "CompanyId" integer NOT NULL REFERENCES "Companies" ("Id")
                                 )
                                 """, migrationConnection))
                {
                    var exception = await Assert.ThrowsAsync<PostgresException>(
                        () => createChild.ExecuteNonQueryAsync());
                    Assert.Equal("55P03", exception.SqlState);
                }

                await using (var checkPartialTable = new NpgsqlCommand(
                                 "SELECT NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = current_schema() AND c.relname = 'CompanyReferences')",
                                 migrationConnection))
                {
                    Assert.True((bool)(await checkPartialTable.ExecuteScalarAsync())!);
                }

                await blockerTransaction.RollbackAsync();

                await using (var retryCreateChild = new NpgsqlCommand("""
                                 CREATE TABLE "CompanyReferences" (
                                     "Id" integer PRIMARY KEY,
                                     "CompanyId" integer NOT NULL REFERENCES "Companies" ("Id")
                                 )
                                 """, migrationConnection))
                {
                    await retryCreateChild.ExecuteNonQueryAsync();
                }

                await using (var checkRetriedTable = new NpgsqlCommand(
                                 "SELECT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = current_schema() AND c.relname = 'CompanyReferences')",
                                 migrationConnection))
                {
                    Assert.True((bool)(await checkRetriedTable.ExecuteScalarAsync())!);
                }
            }
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var dropSchema = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await dropSchema.ExecuteNonQueryAsync();
        }
    }
}
