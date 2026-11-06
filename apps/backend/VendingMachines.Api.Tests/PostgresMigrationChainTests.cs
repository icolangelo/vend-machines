using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class PostgresMigrationChainTests
{
    private const string BootstrapMigration = "20260927163720_BootstrapPlatformAdmin";

    [Fact]
    public async Task ProductionInitializerFailsClosedWithoutApplyingPendingMigrations()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste de inicialização sem migrations exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var databaseName = $"vendmachines_no_startup_migration_{Guid.NewGuid():N}";
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

            await using var db = new AppDbContext(options);
            var migrations = db.Database.GetMigrations().ToArray();
            Assert.NotEmpty(migrations);
            await db.GetService<IMigrator>().MigrateAsync(migrations[0]);

            var pendingBeforeInitialization = (await db.Database.GetPendingMigrationsAsync()).ToArray();
            Assert.NotEmpty(pendingBeforeInitialization);

            var exception = Assert.Throws<InvalidOperationException>(
                () => DbInitializer.Initialize(db, seedDemoData: false));

            Assert.Contains(
                $"Há {pendingBeforeInitialization.Length} migração(ões) pendente(s)",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Equal(pendingBeforeInitialization, await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminConnection.ConnectionString);
            await admin.OpenAsync();
            await using var dropDatabase = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ProductionInitializerPreservesLiveMachineConnectionWhenSchemaIsCurrent()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste de preservação da telemetria no startup exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var databaseName = $"vendmachines_startup_presence_{Guid.NewGuid():N}";
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
            await using var db = new AppDbContext(options);
            var migrations = db.Database.GetMigrations().ToArray();
            var bootstrapIndex = Array.IndexOf(migrations, BootstrapMigration);
            Assert.True(bootstrapIndex > 0, "The reserved administrator bootstrap migration must follow the base schema.");
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[bootstrapIndex - 1]);

            var bootstrapUserId = Guid.NewGuid();
            await using (var seedConnection = new NpgsqlConnection(databaseConnection.ConnectionString))
            {
                await seedConnection.OpenAsync();
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO "Users"
                        ("Id", "Name", "Email", "NormalizedEmail", "PasswordHash", "Role", "CompanyId",
                         "CreatedAt", "Cpf", "NormalizedCpf", "AcceptedPrivacyPolicy", "IsActive", "CredentialVersion")
                    VALUES
                        (@id, 'Synthetic startup owner', 'dev.ivan@gmail.com', 'dev.ivan@gmail.com',
                         'synthetic-only', 'Admin', NULL, CURRENT_TIMESTAMP, '', '', FALSE, TRUE, 0)
                    """, seedConnection);
                seed.Parameters.AddWithValue("id", bootstrapUserId);
                await seed.ExecuteNonQueryAsync();
            }

            await migrator.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());

            var company = new Company
            {
                Name = "Synthetic live telemetry company",
                CreatedByUserId = bootstrapUserId,
                Cnpj = "11222333000181",
                NormalizedCnpj = "11222333000181",
                ActivationStatus = CompanyActivationStatuses.Active
            };
            var machine = new Machine
            {
                Id = "startup-live-machine",
                Name = "Synthetic online machine",
                SerialNumber = "SERIAL-STARTUP-LIVE",
                NormalizedSerialNumber = "SERIAL-STARTUP-LIVE",
                Company = company
            };
            var currentTimeTicks = DateTime.UtcNow.Ticks;
            var postgresPrecisionNow = new DateTime(currentTimeTicks - currentTimeTicks % 10, DateTimeKind.Utc);
            var connectedAt = postgresPrecisionNow.AddMinutes(-3);
            var lastSeenAt = postgresPrecisionNow.AddSeconds(-10);
            var connection = new MachineConnectionState
            {
                MachineId = machine.Id,
                CompanyId = company.Id,
                IsOnline = true,
                ConnectionId = "synthetic-live-connection",
                OwnerNodeId = "synthetic-owner-node",
                OwnerConnectionId = "synthetic-owner-connection",
                CommandRoutingVersion = TelemetryConnectionManager.CurrentCommandRoutingVersion,
                OwnerLeaseExpiresAt = DateTime.UtcNow.AddMinutes(1),
                ConnectedAt = connectedAt,
                LastSeenAt = lastSeenAt
            };
            db.Companies.Add(company);
            db.Machines.Add(machine);
            db.MachineConnectionStates.Add(connection);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            DbInitializer.Initialize(db, seedDemoData: false);

            var persisted = await db.MachineConnectionStates.AsNoTracking().SingleAsync(state => state.MachineId == machine.Id);
            Assert.True(persisted.IsOnline);
            Assert.Equal("synthetic-live-connection", persisted.ConnectionId);
            Assert.Equal("synthetic-owner-node", persisted.OwnerNodeId);
            Assert.Equal("synthetic-owner-connection", persisted.OwnerConnectionId);
            Assert.Equal(TelemetryConnectionManager.CurrentCommandRoutingVersion, persisted.CommandRoutingVersion);
            Assert.NotNull(persisted.OwnerLeaseExpiresAt);
            Assert.Equal(connectedAt, persisted.ConnectedAt);
            Assert.Equal(lastSeenAt, persisted.LastSeenAt);

            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "MachineConnectionStates"
                SET "ConnectionId" = 'synthetic-legacy-reconnected-connection',
                    "LastSeenAt" = CURRENT_TIMESTAMP
                WHERE "MachineId" = 'startup-live-machine'
                """);
            db.ChangeTracker.Clear();

            var legacyReconnect = await db.MachineConnectionStates.AsNoTracking()
                .SingleAsync(state => state.MachineId == "startup-live-machine");
            Assert.True(legacyReconnect.IsOnline);
            Assert.Equal("synthetic-legacy-reconnected-connection", legacyReconnect.ConnectionId);
            Assert.Null(legacyReconnect.OwnerNodeId);
            Assert.Null(legacyReconnect.OwnerConnectionId);
            Assert.Null(legacyReconnect.CommandRoutingVersion);
            Assert.Null(legacyReconnect.OwnerLeaseExpiresAt);
            Assert.Empty(await db.TelemetryEvents.AsNoTracking().ToListAsync());
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminConnection.ConnectionString);
            await admin.OpenAsync();
            await using var dropDatabase = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData("Admin", true)]
    [InlineData("PlatformAdmin", false)]
    public async Task FullMigrationChainAuditsReservedBootstrapAccount(string initialRole, bool expectedRoleChange)
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste da cadeia de migrations exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

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
                         'synthetic-only', @role, NULL, CURRENT_TIMESTAMP, '', '', FALSE, TRUE, 0);
                    """, seedConnection);
                seed.Parameters.AddWithValue("role", initialRole);
                await seed.ExecuteNonQueryAsync();

                await migrator.MigrateAsync();
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }

            await using var verifyConnection = new NpgsqlConnection(databaseConnection.ConnectionString);
            await verifyConnection.OpenAsync();
            await using var verify = new NpgsqlCommand("""
                SELECT u."Role", u."CredentialVersion",
                       (SELECT COUNT(*) FROM "AuditLogs" a WHERE a."Action" = 'auth.platform_admin_bootstrap_completed'),
                       (SELECT a."MetadataJson"::jsonb ->> 'roleChanged' FROM "AuditLogs" a
                        WHERE a."Action" = 'auth.platform_admin_bootstrap_completed')
                FROM "Users" u
                WHERE u."NormalizedEmail" = 'dev.ivan@gmail.com'
                """, verifyConnection);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("PlatformAdmin", reader.GetString(0));
            Assert.Equal(expectedRoleChange ? 1 : 0, reader.GetInt32(1));
            Assert.Equal(1, reader.GetInt64(2));
            Assert.Equal(expectedRoleChange ? "true" : "false", reader.GetString(3));

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
                       s."IsOnline", s."ConnectionId", s."OwnerNodeId", s."OwnerConnectionId",
                       s."CommandRoutingVersion"
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
                Assert.True(await legacyReader.IsDBNullAsync(8));
            }

            var uncertainLocalCommandId = Guid.NewGuid();
            await ExecuteLegacyWriteAsync("""
                INSERT INTO "TelemetryCommands"
                    ("Id", "CompanyId", "MachineId", "MessageId", "Direction", "Command", "Status", "Attempts", "CreatedAt", "Error")
                VALUES (@id, @company_id, @machine_id, 101, 'server_to_device', 'ABRIR_SESSAO', 'failed', 3, now(), 'ack_timeout')
                """, ("id", uncertainLocalCommandId), ("company_id", legacyCompanyId), ("machine_id", legacyMachineId));
            await ExecuteLegacyWriteAsync("""
                INSERT INTO "TelemetryCommandResolutions"
                    ("Id", "TelemetryCommandId", "TelemetryCommandDispatchId", "OperatorName", "Reason", "EvidenceReference", "CreatedAt")
                VALUES (@id, @command_id, NULL, 'synthetic-operator', 'estado conferido', 'synthetic-ticket', now())
                """, ("id", Guid.NewGuid()), ("command_id", uncertainLocalCommandId));

            var knownFailureCommandId = Guid.NewGuid();
            await ExecuteLegacyWriteAsync("""
                INSERT INTO "TelemetryCommands"
                    ("Id", "CompanyId", "MachineId", "MessageId", "Direction", "Command", "Status", "Attempts", "CreatedAt", "Error")
                VALUES (@id, @company_id, @machine_id, 102, 'server_to_device', 'ABRIR_SESSAO', 'failed', 0, now(), 'machine_offline')
                """, ("id", knownFailureCommandId), ("company_id", legacyCompanyId), ("machine_id", legacyMachineId));
            var invalidLocalResolution = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await ExecuteLegacyWriteAsync("""
                    INSERT INTO "TelemetryCommandResolutions"
                        ("Id", "TelemetryCommandId", "TelemetryCommandDispatchId", "OperatorName", "Reason", "EvidenceReference", "CreatedAt")
                    VALUES (@id, @command_id, NULL, 'synthetic-operator', 'estado conferido', 'synthetic-ticket', now())
                    """, ("id", Guid.NewGuid()), ("command_id", knownFailureCommandId));
            });
            Assert.Equal("P0001", invalidLocalResolution.SqlState);
            Assert.Equal("telemetry_command_not_unresolved", invalidLocalResolution.MessageText);

            var dispatchId = Guid.NewGuid();
            await ExecuteLegacyWriteAsync("""
                INSERT INTO "TelemetryCommandDispatches"
                    ("Id", "CompanyId", "MachineId", "OwnerNodeId", "Operation", "TimeoutMilliseconds", "PayloadJson", "Status", "CreatedAt")
                VALUES (@id, @company_id, @machine_id, 'synthetic-owner', 'Command', 15000, '{"command":"open"}', 'Pending', now())
                """, ("id", dispatchId), ("company_id", legacyCompanyId), ("machine_id", legacyMachineId));
            var pendingDispatchResolution = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await ExecuteLegacyWriteAsync("""
                    INSERT INTO "TelemetryCommandResolutions"
                        ("Id", "TelemetryCommandId", "TelemetryCommandDispatchId", "OperatorName", "Reason", "EvidenceReference", "CreatedAt")
                    VALUES (@id, NULL, @dispatch_id, 'synthetic-operator', 'estado conferido', 'synthetic-ticket', now())
                    """, ("id", Guid.NewGuid()), ("dispatch_id", dispatchId));
            });
            Assert.Equal("telemetry_command_not_unresolved", pendingDispatchResolution.MessageText);
            await ExecuteLegacyWriteAsync(
                "UPDATE \"TelemetryCommandDispatches\" SET \"Status\" = 'Indeterminate' WHERE \"Id\" = @id",
                ("id", dispatchId));
            await ExecuteLegacyWriteAsync("""
                INSERT INTO "TelemetryCommandResolutions"
                    ("Id", "TelemetryCommandId", "TelemetryCommandDispatchId", "OperatorName", "Reason", "EvidenceReference", "CreatedAt")
                VALUES (@id, NULL, @dispatch_id, 'synthetic-operator', 'estado conferido', 'synthetic-ticket', now())
                """, ("id", Guid.NewGuid()), ("dispatch_id", dispatchId));

            var failedDispatchId = Guid.NewGuid();
            await ExecuteLegacyWriteAsync("""
                INSERT INTO "TelemetryCommandDispatches"
                    ("Id", "CompanyId", "MachineId", "OwnerNodeId", "Operation", "TimeoutMilliseconds", "PayloadJson", "Status", "CreatedAt", "Error")
                VALUES (@id, @company_id, @machine_id, 'synthetic-owner', 'Command', 15000, '{"command":"open"}', 'Failed', now(), 'ack_timeout')
                """, ("id", failedDispatchId), ("company_id", legacyCompanyId), ("machine_id", legacyMachineId));
            await ExecuteLegacyWriteAsync("""
                INSERT INTO "TelemetryCommandResolutions"
                    ("Id", "TelemetryCommandId", "TelemetryCommandDispatchId", "OperatorName", "Reason", "EvidenceReference", "CreatedAt")
                VALUES (@id, NULL, @dispatch_id, 'synthetic-operator', 'estado conferido', 'synthetic-ticket', now())
                """, ("id", Guid.NewGuid()), ("dispatch_id", failedDispatchId));

            var openingSessionId = Guid.NewGuid();
            await using (var admissionConnection = new NpgsqlConnection(databaseConnection.ConnectionString))
            {
                await admissionConnection.OpenAsync();
                await using var admissionTransaction = await admissionConnection.BeginTransactionAsync();
                await using (var lockGate = new NpgsqlCommand(
                                 "SELECT \"NewSessionsPaused\" FROM \"SessionAdmissionGates\" WHERE \"Id\" = 1 FOR SHARE",
                                 admissionConnection,
                                 admissionTransaction))
                    Assert.False((bool)(await lockGate.ExecuteScalarAsync())!);

                await using (var insertOpeningSession = new NpgsqlCommand("""
                    INSERT INTO "MachineSessions"
                        ("Id", "MachineId", "CompanyId", "Source", "State", "StartedAt", "LastEventAt")
                    VALUES (@id, @machine_id, @company_id, 'panel', 'Opening', now(), now())
                    """, admissionConnection, admissionTransaction))
                {
                    insertOpeningSession.Parameters.AddWithValue("id", openingSessionId);
                    insertOpeningSession.Parameters.AddWithValue("machine_id", legacyMachineId);
                    insertOpeningSession.Parameters.AddWithValue("company_id", legacyCompanyId);
                    Assert.Equal(1, await insertOpeningSession.ExecuteNonQueryAsync());
                }

                await using var pauseConnection = new NpgsqlConnection(databaseConnection.ConnectionString);
                await pauseConnection.OpenAsync();
                var pauseBackendPid = pauseConnection.ProcessID;
                var pauseTask = new NpgsqlCommand("""
                    UPDATE "SessionAdmissionGates"
                    SET "NewSessionsPaused" = TRUE, "UpdatedAt" = now(), "UpdatedBy" = 'test', "Reason" = 'race test'
                    WHERE "Id" = 1
                    """, pauseConnection).ExecuteNonQueryAsync();
                await using var observerConnection = new NpgsqlConnection(databaseConnection.ConnectionString);
                await observerConnection.OpenAsync();
                var lockWaitDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                var pauseIsWaitingOnLock = false;
                while (DateTime.UtcNow < lockWaitDeadline && !pauseTask.IsCompleted)
                {
                    await using var checkLockWait = new NpgsqlCommand(
                        "SELECT wait_event_type = 'Lock' FROM pg_stat_activity WHERE pid = @pid",
                        observerConnection);
                    checkLockWait.Parameters.AddWithValue("pid", pauseBackendPid);
                    pauseIsWaitingOnLock = await checkLockWait.ExecuteScalarAsync() as bool? == true;
                    if (pauseIsWaitingOnLock) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(25));
                }
                Assert.True(pauseIsWaitingOnLock, "O UPDATE de pausa não foi observado aguardando o lock da abertura admitida.");
                Assert.False(pauseTask.IsCompleted, "A pausa precisa esperar a gravação de abertura já admitida.");
                await admissionTransaction.CommitAsync();
                Assert.Equal(1, await pauseTask);
            }

            var blockedSessionId = Guid.NewGuid();
            var triggerFailure = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var blockedInsert = new NpgsqlCommand("""
                    INSERT INTO "MachineSessions"
                        ("Id", "MachineId", "CompanyId", "Source", "State", "StartedAt", "LastEventAt")
                    VALUES (@id, @machine_id, @company_id, 'panel', 'Opening', now(), now())
                    """, legacyWriteConnection);
                blockedInsert.Parameters.AddWithValue("id", blockedSessionId);
                blockedInsert.Parameters.AddWithValue("machine_id", legacyMachineId);
                blockedInsert.Parameters.AddWithValue("company_id", legacyCompanyId);
                await blockedInsert.ExecuteNonQueryAsync();
            });
            Assert.Equal("P0001", triggerFailure.SqlState);
            Assert.Equal("deployment_draining", triggerFailure.MessageText);

            await using (var closeAdmittedSession = new NpgsqlCommand(
                             "UPDATE \"MachineSessions\" SET \"ClosedAt\" = now(), \"State\" = 'Completed' WHERE \"Id\" = @id",
                             legacyWriteConnection))
            {
                closeAdmittedSession.Parameters.AddWithValue("id", openingSessionId);
                Assert.Equal(1, await closeAdmittedSession.ExecuteNonQueryAsync());
            }

            var remainingActiveSessions = await new NpgsqlCommand(
                "SELECT count(*) FROM \"MachineSessions\" WHERE \"ClosedAt\" IS NULL",
                legacyWriteConnection).ExecuteScalarAsync();
            Assert.Equal(0L, (long)remainingActiveSessions!);

            // The origin/develop initializer clears every presence row on startup.
            await ExecuteLegacyWriteAsync("""
                UPDATE "MachineConnectionStates"
                SET "IsOnline" = TRUE, "ConnectionId" = 'candidate-socket',
                    "OwnerNodeId" = 'candidate-node', "OwnerConnectionId" = 'candidate-socket'
                WHERE "MachineId" = @machine_id
                """, ("machine_id", legacyMachineId));
            await ExecuteLegacyWriteAsync("""
                UPDATE "MachineConnectionStates"
                SET "IsOnline" = FALSE, "ConnectionId" = NULL, "DisconnectedAt" = CURRENT_TIMESTAMP
                """);
            await using (var afterOldStartup = new NpgsqlCommand("""
                SELECT "IsOnline", "ConnectionId", "OwnerNodeId", "OwnerConnectionId"
                FROM "MachineConnectionStates" WHERE "MachineId" = @machine_id
                """, legacyWriteConnection))
            {
                afterOldStartup.Parameters.AddWithValue("machine_id", legacyMachineId);
                await using var readerAfterStartup = await afterOldStartup.ExecuteReaderAsync();
                Assert.True(await readerAfterStartup.ReadAsync());
                Assert.False(readerAfterStartup.GetBoolean(0));
                Assert.True(await readerAfterStartup.IsDBNullAsync(1));
                Assert.True(await readerAfterStartup.IsDBNullAsync(2));
                Assert.True(await readerAfterStartup.IsDBNullAsync(3));
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
