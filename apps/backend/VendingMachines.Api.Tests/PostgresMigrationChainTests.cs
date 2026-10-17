using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using VendingMachines.Api.Data;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class PostgresMigrationChainTests
{
    private const string BootstrapMigration = "20260927163720_BootstrapPlatformAdmin";

    [Fact]
    public async Task FullMigrationChainAppliesWhenReservedBootstrapAccountExists()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection)) return;

        var databaseName = $"vendmachines_migration_{Guid.NewGuid():N}";
        var adminConnection = new NpgsqlConnectionStringBuilder(configuredConnection) { Database = "postgres" };
        var databaseConnection = new NpgsqlConnectionStringBuilder(configuredConnection) { Database = databaseName };

        await using (var admin = new NpgsqlConnection(adminConnection.ConnectionString))
        {
            await admin.OpenAsync();
            await using var createDatabase = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await createDatabase.ExecuteNonQueryAsync();
        }

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(databaseConnection.ConnectionString)
                .Options;

            await using (var db = new AppDbContext(options))
            {
                var migrations = db.Database.GetMigrations().ToArray();
                var bootstrapIndex = Array.IndexOf(migrations, BootstrapMigration);
                Assert.True(bootstrapIndex > 0, "The reserved administrator bootstrap migration must follow the base schema.");

                var migrator = db.GetService<IMigrator>();
                await migrator.MigrateAsync(migrations[bootstrapIndex - 1]);

                await using var seedConnection = new NpgsqlConnection(databaseConnection.ConnectionString);
                await seedConnection.OpenAsync();
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO "Users"
                        ("Id", "Name", "Email", "NormalizedEmail", "PasswordHash", "Role", "CompanyId",
                         "CreatedAt", "Cpf", "NormalizedCpf", "AcceptedPrivacyPolicy", "IsActive", "CredentialVersion")
                    VALUES
                        (gen_random_uuid(), 'Synthetic bootstrap account', 'dev.ivan@gmail.com', 'dev.ivan@gmail.com',
                         'synthetic-only', 'Admin', NULL, CURRENT_TIMESTAMP, '', '', FALSE, TRUE, 0);
                    """, seedConnection);
                await seed.ExecuteNonQueryAsync();

                await migrator.MigrateAsync();
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }

            await using var verifyConnection = new NpgsqlConnection(databaseConnection.ConnectionString);
            await verifyConnection.OpenAsync();
            await using var verify = new NpgsqlCommand("""
                SELECT u."Role", u."CredentialVersion",
                       (SELECT COUNT(*) FROM "AuditLogs" a WHERE a."Action" = 'auth.platform_admin_bootstrap_promoted')
                FROM "Users" u
                WHERE u."NormalizedEmail" = 'dev.ivan@gmail.com'
                """, verifyConnection);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("PlatformAdmin", reader.GetString(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal(1, reader.GetInt64(2));

            var legacyCompanyId = Guid.NewGuid();
            var legacyMachineId = $"legacy-{Guid.NewGuid():N}";
            var legacySerial = $"LEGACY-{Guid.NewGuid():N}".ToUpperInvariant();
            var legacyConnectionId = $"legacy-connection-{Guid.NewGuid():N}";
            await using var legacyWriteConnection = new NpgsqlConnection(databaseConnection.ConnectionString);
            await legacyWriteConnection.OpenAsync();
            // These writes use only columns present in the version at HEAD, simulating
            // the old binary while every migration in the candidate has been applied.
            async Task ExecuteLegacyWriteAsync(string sql, params (string Name, object Value)[] parameters)
            {
                await using var command = new NpgsqlCommand(sql, legacyWriteConnection);
                foreach (var (name, value) in parameters)
                    command.Parameters.AddWithValue(name, value);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            await ExecuteLegacyWriteAsync("""
                WITH platform_user AS (
                    SELECT "Id" FROM "Users" WHERE "NormalizedEmail" = 'dev.ivan@gmail.com'
                )
                INSERT INTO "Companies" ("Id", "Name", "CreatedByUserId", "CreatedAt", "Cnpj")
                SELECT @company_id, 'Empresa criada pela API antiga', "Id", CURRENT_TIMESTAMP, @cnpj
                FROM platform_user
                """, ("company_id", legacyCompanyId), ("cnpj", "11222333000181"));
            var legacyEmail = $"legacy-{Guid.NewGuid():N}@example.test";
            await ExecuteLegacyWriteAsync("""
                INSERT INTO "Users"
                    ("Id", "Name", "Email", "PasswordHash", "Role", "CompanyId", "CreatedAt", "Cpf", "AcceptedPrivacyPolicy")
                VALUES
                    (@user_id, 'Responsável legado', @email, 'synthetic-only', 'Admin', @company_id, CURRENT_TIMESTAMP, @cpf, FALSE)
                """, ("user_id", Guid.NewGuid()), ("email", legacyEmail), ("company_id", legacyCompanyId), ("cpf", "12345678901"));
            await ExecuteLegacyWriteAsync("""
                INSERT INTO "Machines"
                    ("Id", "Name", "ClientName", "Location", "Status", "StockLevel", "Revenue30d", "TotalSales30d",
                     "LastSync", "SerialNumber", "NormalizedSerialNumber", "CompanyId", "MercadoPagoEnabled", "AutoOpenSessionEnabled")
                VALUES
                    (@machine_id, 'Máquina legado', '', '', 'online', 0, 0, 0, '', @serial, @serial, @company_id, FALSE, FALSE)
                """, ("machine_id", legacyMachineId), ("serial", legacySerial), ("company_id", legacyCompanyId));
            await ExecuteLegacyWriteAsync("""
                INSERT INTO "MachineConnectionStates"
                    ("Id", "MachineId", "CompanyId", "MonitoringEnabled", "IsOnline", "ConnectionId", "ConnectedAt", "LastSeenAt")
                VALUES
                    (@state_id, @machine_id, @company_id, TRUE, TRUE, @connection_id, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
                """, ("state_id", Guid.NewGuid()), ("machine_id", legacyMachineId), ("company_id", legacyCompanyId), ("connection_id", legacyConnectionId));
            await ExecuteLegacyWriteAsync("""
                UPDATE "MachineConnectionStates"
                SET "OwnerNodeId" = 'new-api-node', "OwnerConnectionId" = 'new-api-connection'
                WHERE "MachineId" = @machine_id
                """, ("machine_id", legacyMachineId));
            var reconnectedConnectionId = $"reconnected-{Guid.NewGuid():N}";
            await ExecuteLegacyWriteAsync("""
                UPDATE "MachineConnectionStates"
                SET "ConnectionId" = @reconnected_id, "LastSeenAt" = CURRENT_TIMESTAMP
                WHERE "MachineId" = @machine_id
                """, ("reconnected_id", reconnectedConnectionId), ("machine_id", legacyMachineId));
            await using (var legacyRead = new NpgsqlCommand("""
                SELECT c."ActivationStatus", c."NormalizedCnpj", u."IsActive", u."NormalizedEmail",
                       s."IsOnline", s."ConnectionId", s."OwnerNodeId", s."OwnerConnectionId"
                FROM "Companies" c
                JOIN "Users" u ON u."CompanyId" = c."Id"
                JOIN "MachineConnectionStates" s ON s."CompanyId" = c."Id"
                WHERE c."Id" = @company_id AND s."MachineId" = @machine_id
                """, legacyWriteConnection))
            {
                legacyRead.Parameters.AddWithValue("company_id", legacyCompanyId);
                legacyRead.Parameters.AddWithValue("machine_id", legacyMachineId);
                await using var legacyReader = await legacyRead.ExecuteReaderAsync();
                Assert.True(await legacyReader.ReadAsync());
                Assert.Equal("Pending", legacyReader.GetString(0));
                Assert.Equal("11222333000181", legacyReader.GetString(1));
                Assert.False(legacyReader.GetBoolean(2));
                Assert.Equal(legacyEmail, legacyReader.GetString(3));
                Assert.True(legacyReader.GetBoolean(4));
                Assert.Equal(reconnectedConnectionId, legacyReader.GetString(5));
                Assert.True(await legacyReader.IsDBNullAsync(6));
                Assert.True(await legacyReader.IsDBNullAsync(7));
            }
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminConnection.ConnectionString);
            await admin.OpenAsync();
            await using var dropDatabase = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }
}
