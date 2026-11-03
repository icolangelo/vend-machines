using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class DeviceMessageInboxTests
{
    [Fact]
    public async Task PostgresInboxRejectsOldOwnerAndPreservesOrderAfterReconnectWithEarlierClock()
    {
        var configured = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste de propriedade MDB exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }
        var database = $"vend_inbox_owner_{Guid.NewGuid():N}";
        var adminString = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", Pooling = false }.ConnectionString;
        var testString = new NpgsqlConnectionStringBuilder(configured) { Database = database, Pooling = false }.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(testString).Options;
            var company = Guid.NewGuid();
            var clock = DateTime.UtcNow;
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Companies.Add(new Company { Id = company, Name = "Empresa sintética" });
                setup.Machines.Add(new Machine { Id = "owner-test", Name = "Máquina", SerialNumber = "OWNER-TEST", NormalizedSerialNumber = "OWNER-TEST", CompanyId = company });
                setup.MachineConnectionStates.Add(new MachineConnectionState {
                    MachineId = "owner-test", CompanyId = company, IsOnline = true, ConnectionId = "connection-a", OwnerConnectionId = "connection-a", OwnerNodeId = "node-a", CommandRoutingVersion = TelemetryConnectionManager.CurrentCommandRoutingVersion, LastSeenAt = clock
                });
                await setup.SaveChangesAsync();
            }
            async Task<bool> Persist(InboundDeviceMessage message)
            {
                await using var db = new AppDbContext(options);
                return await CreateInboxService(db).PersistBeforeAcknowledgementAsync(message, CancellationToken.None);
            }
            var first = new InboundDeviceMessage("owner-test", company, "OWNER-TEST", "connection-a", 1, "{\"command\":\"pool\",\"type\":\"begin_session\"}", clock, "node-a");
            Assert.False(await Persist(first with { OwnerNodeId = null }));
            Assert.False(await Persist(first with { OwnerNodeId = " " }));
            Assert.True(await Persist(first));
            await using (var switchOwner = new AppDbContext(options))
            {
                await switchOwner.MachineConnectionStates.Where(row => row.MachineId == "owner-test").ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.ConnectionId, "connection-b")
                    .SetProperty(row => row.OwnerConnectionId, "connection-b")
                    .SetProperty(row => row.OwnerNodeId, "node-b"));
            }
            Assert.False(await Persist(first with { MessageId = 2 }));
            var second = first with { ConnectionId = "connection-b", OwnerNodeId = "node-b", MessageId = 2, ReceivedAt = clock.AddMinutes(-5), DataJson = "{\"command\":\"pool\",\"type\":\"end_session\"}" };
            Assert.True(await Persist(second));
            await using var verify = new AppDbContext(options);
            var messages = await verify.DeviceMessageInbox.AsNoTracking().OrderBy(row => row.ReceivedAt).ToListAsync();
            Assert.Equal(2, messages.Count);
            Assert.Equal(new[] { 1, 2 }, messages.Select(row => row.MessageId).ToArray());
            Assert.True(messages[1].ReceivedAt > messages[0].ReceivedAt);

            await using var switchConnection = new NpgsqlConnection(testString);
            await switchConnection.OpenAsync();
            await using var switchTransaction = await switchConnection.BeginTransactionAsync();
            await using (var switchCommand = new NpgsqlCommand("""
                UPDATE "MachineConnectionStates"
                SET "ConnectionId" = 'connection-c', "OwnerConnectionId" = 'connection-c', "OwnerNodeId" = 'node-c'
                WHERE "MachineId" = 'owner-test'
                """, switchConnection, switchTransaction))
                Assert.Equal(1, await switchCommand.ExecuteNonQueryAsync());
            var racingMessage = second with { MessageId = 3, DataJson = "{\"command\":\"pool\",\"type\":\"cancel\"}" };
            var oldOwnerAttempt = Persist(racingMessage);
            var waiting = false;
            await using (var observer = new NpgsqlConnection(testString))
            {
                await observer.OpenAsync();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && !oldOwnerAttempt.IsCompleted)
                {
                    await using var query = new NpgsqlCommand("SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query ILIKE '%MachineConnectionStates%FOR UPDATE%'", observer);
                    waiting = Convert.ToInt32(await query.ExecuteScalarAsync()) > 0;
                    if (waiting) break;
                    await Task.Delay(25);
                }
            }
            await switchTransaction.CommitAsync();
            Assert.False(await oldOwnerAttempt.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(waiting, "A mensagem do antigo proprietário deve aguardar a transação de troca antes de ser verificada.");
            Assert.True(await Persist(racingMessage with { ConnectionId = "connection-c", OwnerNodeId = "node-c" }));
            Assert.Equal(3, await verify.DeviceMessageInbox.AsNoTracking().CountAsync());

            await using var barrier = new NpgsqlConnection(testString);
            await barrier.OpenAsync();
            await using (var setupBarrier = new NpgsqlCommand("""
                CREATE FUNCTION inbox_owner_test_barrier() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN PERFORM pg_advisory_xact_lock(738191, 4394); RETURN NEW; END $$;
                CREATE TRIGGER inbox_owner_test_barrier BEFORE INSERT ON "DeviceMessageInbox"
                FOR EACH ROW EXECUTE FUNCTION inbox_owner_test_barrier();
                SELECT pg_advisory_lock(738191, 4394);
                """, barrier))
                await setupBarrier.ExecuteNonQueryAsync();
            var beforeSwitch = racingMessage with { ConnectionId = "connection-c", OwnerNodeId = "node-c", MessageId = 4, DataJson = "{\"command\":\"pool\",\"type\":\"begin_session\"}" };
            var pendingMessage = Persist(beforeSwitch);
            var insertWaiting = false;
            await using var raceObserver = new NpgsqlConnection(testString);
            await raceObserver.OpenAsync();
            var insertDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < insertDeadline && !pendingMessage.IsCompleted)
            {
                await using var query = new NpgsqlCommand("SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query ILIKE '%INSERT INTO \"DeviceMessageInbox\"%'", raceObserver);
                insertWaiting = Convert.ToInt32(await query.ExecuteScalarAsync()) > 0;
                if (insertWaiting) break;
                await Task.Delay(25);
            }
            async Task<int> SwitchAgain()
            {
                await using var db = new AppDbContext(options);
                return await db.MachineConnectionStates.Where(row => row.MachineId == "owner-test").ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.ConnectionId, "connection-d")
                    .SetProperty(row => row.OwnerConnectionId, "connection-d")
                    .SetProperty(row => row.OwnerNodeId, "node-d"));
            }
            var pendingSwitch = SwitchAgain();
            var switchWaiting = false;
            var switchDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < switchDeadline && !pendingSwitch.IsCompleted)
            {
                await using var query = new NpgsqlCommand("SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query ILIKE '%UPDATE \"MachineConnectionStates\"%'", raceObserver);
                switchWaiting = Convert.ToInt32(await query.ExecuteScalarAsync()) > 0;
                if (switchWaiting) break;
                await Task.Delay(25);
            }
            await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(738191, 4394)", barrier))
                await release.ExecuteScalarAsync();
            Assert.True(await pendingMessage.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, await pendingSwitch.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(insertWaiting && switchWaiting, "A inserção deve segurar o lock do proprietário enquanto a troca espera.");
            Assert.Equal(4, await verify.DeviceMessageInbox.AsNoTracking().CountAsync());
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task PersistBeforeAcknowledgement_IsIdempotentAcrossConnections()
    {
        await using var connection = await CreateDatabaseAsync();
        var machineId = $"machine-{Guid.NewGuid():N}";
        var companyId = Guid.NewGuid();
        var original = new InboundDeviceMessage(machineId, companyId, "SERIAL-1", "connection-one", 42,
            "{\"command\":\"vend\",\"type\":\"success\"}", DateTime.UtcNow);
        var retry = original with { ConnectionId = "connection-two", ReceivedAt = original.ReceivedAt.AddMinutes(1) };

        await using var db = CreateContext(connection);
        db.Companies.Add(new VendingMachines.Api.Models.Company { Id = companyId, Name = "Inbox test", Cnpj = "00000000000000" });
        db.Machines.Add(new VendingMachines.Api.Models.Machine
        {
            Id = machineId, CompanyId = companyId, Name = "Inbox test machine", SerialNumber = original.Serial
        });
        db.MachineSessions.Add(new VendingMachines.Api.Models.MachineSession
        {
            MachineId = machineId, CompanyId = companyId, State = MachineSessionStates.AwaitingSelection
        });
        await db.SaveChangesAsync();
        var inbox = CreateInboxService(db);
        await inbox.PersistBeforeAcknowledgementAsync(original, CancellationToken.None);
        await inbox.PersistBeforeAcknowledgementAsync(retry, CancellationToken.None);

        var saved = await db.DeviceMessageInbox.AsNoTracking().SingleAsync();
        Assert.Equal(original.DataJson, saved.DataJson);
        Assert.Equal(original.ConnectionId, saved.ConnectionId);
        Assert.Equal("Pending", saved.Status);
    }

    [Fact]
    public async Task PersistBeforeAcknowledgement_DoesNotDeduplicateNewConnectionWithoutActiveSession()
    {
        await using var connection = await CreateDatabaseAsync();
        await using var db = CreateContext(connection);
        var inbox = CreateInboxService(db);
        var message = new InboundDeviceMessage($"machine-{Guid.NewGuid():N}", Guid.NewGuid(), "SERIAL-REBOOT",
            "connection-one", 1, "{\"command\":\"pool\",\"type\":\"begin_session\"}", DateTime.UtcNow);

        await inbox.PersistBeforeAcknowledgementAsync(message, CancellationToken.None);
        await inbox.PersistBeforeAcknowledgementAsync(message with { ConnectionId = "connection-two" }, CancellationToken.None);

        Assert.Equal(2, await db.DeviceMessageInbox.CountAsync());
    }

    [Fact]
    public async Task PersistBeforeAcknowledgement_DoesNotDeduplicateStatusAfterReconnect()
    {
        await using var connection = await CreateDatabaseAsync();
        await using var db = CreateContext(connection);
        var inbox = CreateInboxService(db);
        var message = new InboundDeviceMessage($"machine-{Guid.NewGuid():N}", Guid.NewGuid(), "SERIAL-STATUS",
            "connection-one", 1, "{\"command\":\"status\",\"status\":\"idle_state\"}", DateTime.UtcNow);

        await inbox.PersistBeforeAcknowledgementAsync(message, CancellationToken.None);
        await inbox.PersistBeforeAcknowledgementAsync(message with { ConnectionId = "connection-two" }, CancellationToken.None);

        Assert.Equal(2, await db.DeviceMessageInbox.CountAsync());
    }

    [Fact]
    public async Task Processor_ResumesPersistedMessageAndMarksItProcessed()
    {
        await using var connection = await CreateDatabaseAsync();
        var original = new InboundDeviceMessage($"machine-{Guid.NewGuid():N}", Guid.NewGuid(), "SERIAL-2",
            "connection-restarted", 7, "{\"command\":\"pool\",\"type\":\"begin_session\"}", DateTime.UtcNow);
        await using (var receivingDb = CreateContext(connection))
        {
            var inbox = CreateInboxService(receivingDb);
            await inbox.PersistBeforeAcknowledgementAsync(original, CancellationToken.None);
        }

        await using var processingDb = CreateContext(connection);
        var handler = new RecordingHandler();
        var processor = new DeviceMessageInboxProcessor(
            processingDb, handler, NullLogger<DeviceMessageInboxProcessor>.Instance);

        Assert.True(await processor.ProcessNextAsync(CancellationToken.None));
        Assert.False(await processor.ProcessNextAsync(CancellationToken.None));
        var saved = await processingDb.DeviceMessageInbox.AsNoTracking().SingleAsync();
        Assert.Equal("Processed", saved.Status);
        Assert.Equal(1, saved.Attempts);
        Assert.Equal(original.DataJson, Assert.Single(handler.Messages).DataJson);
        Assert.Equal(0, await processor.CountUnfinishedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Processor_RetriesFailureAndContinuesInMachineOrder()
    {
        await using var connection = await CreateDatabaseAsync();
        var machineId = $"machine-{Guid.NewGuid():N}";
        var receivedAt = DateTime.UtcNow;
        await using (var db = CreateContext(connection))
        {
            var inbox = CreateInboxService(db);
            await inbox.PersistBeforeAcknowledgementAsync(
                new InboundDeviceMessage(machineId, Guid.NewGuid(), "SERIAL-3", "connection", 1,
                    "{\"command\":\"pool\",\"type\":\"begin_session\"}", receivedAt), CancellationToken.None);
            await inbox.PersistBeforeAcknowledgementAsync(
                new InboundDeviceMessage(machineId, Guid.NewGuid(), "SERIAL-3", "connection", 2,
                    "{\"command\":\"pool\",\"type\":\"end_session\"}", receivedAt.AddTicks(1)), CancellationToken.None);
        }

        await using var processingDb = CreateContext(connection);
        var failFirst = new RecordingHandler(_ => throw new InvalidOperationException("falha temporária"));
        var processor = new DeviceMessageInboxProcessor(
            processingDb, failFirst, NullLogger<DeviceMessageInboxProcessor>.Instance);

        Assert.True(await processor.ProcessNextAsync(CancellationToken.None));
        var first = await processingDb.DeviceMessageInbox.AsNoTracking().OrderBy(row => row.ReceivedAt).FirstAsync();
        var second = await processingDb.DeviceMessageInbox.AsNoTracking().OrderByDescending(row => row.ReceivedAt).FirstAsync();
        Assert.Equal("Retry", first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.Equal("Pending", second.Status);

        await processingDb.DeviceMessageInbox.Where(row => row.Id == first.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.NextAttemptAt, (DateTime?)null));
        var success = new RecordingHandler();
        var retryProcessor = new DeviceMessageInboxProcessor(
            processingDb, success, NullLogger<DeviceMessageInboxProcessor>.Instance);

        Assert.True(await retryProcessor.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(first.Id, (await processingDb.DeviceMessageInbox.AsNoTracking()
            .OrderBy(row => row.ReceivedAt).FirstAsync()).Id);
        Assert.Equal("Processed", (await processingDb.DeviceMessageInbox.AsNoTracking()
            .SingleAsync(row => row.Id == first.Id)).Status);
    }

    [Fact]
    public async Task Processor_OldLeaseFailureCannotOverwriteNewerSuccessfulAttempt()
    {
        await using var connection = await CreateDatabaseAsync();
        var message = new InboundDeviceMessage($"machine-{Guid.NewGuid():N}", Guid.NewGuid(), "SERIAL-LEASE",
            "connection", 1, "{\"command\":\"pool\",\"type\":\"begin_session\"}", DateTime.UtcNow);
        await using (var receivingDb = CreateContext(connection))
            await CreateInboxService(receivingDb).PersistBeforeAcknowledgementAsync(message, CancellationToken.None);

        await using var firstDb = CreateContext(connection);
        await using var secondDb = CreateContext(connection);
        var handler = new LeaseRaceHandler(firstAttemptFails: true);
        var firstProcessor = new DeviceMessageInboxProcessor(
            firstDb, handler, NullLogger<DeviceMessageInboxProcessor>.Instance);
        var secondProcessor = new DeviceMessageInboxProcessor(
            secondDb, handler, NullLogger<DeviceMessageInboxProcessor>.Instance);

        var firstAttempt = firstProcessor.ProcessNextAsync(CancellationToken.None);
        await handler.FirstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await secondDb.DeviceMessageInbox.ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.ProcessingStartedAt, DateTime.UtcNow.AddMinutes(-3)));
        Assert.True(await secondProcessor.ProcessNextAsync(CancellationToken.None));
        handler.ReleaseFirstAttempt.TrySetResult();
        Assert.True(await firstAttempt);

        var saved = await secondDb.DeviceMessageInbox.AsNoTracking().SingleAsync();
        Assert.Equal("Processed", saved.Status);
        Assert.Equal(2, saved.Attempts);
        Assert.Null(saved.ProcessingStartedAt);
        Assert.Null(saved.NextAttemptAt);
        Assert.Equal(0, await secondProcessor.CountUnfinishedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Processor_OldLeaseSuccessCannotOverwriteNewerRetry()
    {
        await using var connection = await CreateDatabaseAsync();
        var message = new InboundDeviceMessage($"machine-{Guid.NewGuid():N}", Guid.NewGuid(), "SERIAL-LEASE-2",
            "connection", 1, "{\"command\":\"pool\",\"type\":\"begin_session\"}", DateTime.UtcNow);
        await using (var receivingDb = CreateContext(connection))
            await CreateInboxService(receivingDb).PersistBeforeAcknowledgementAsync(message, CancellationToken.None);

        await using var firstDb = CreateContext(connection);
        await using var secondDb = CreateContext(connection);
        var handler = new LeaseRaceHandler(firstAttemptFails: false);
        var firstProcessor = new DeviceMessageInboxProcessor(
            firstDb, handler, NullLogger<DeviceMessageInboxProcessor>.Instance);
        var secondProcessor = new DeviceMessageInboxProcessor(
            secondDb, handler, NullLogger<DeviceMessageInboxProcessor>.Instance);

        var firstAttempt = firstProcessor.ProcessNextAsync(CancellationToken.None);
        await handler.FirstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await secondDb.DeviceMessageInbox.ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.ProcessingStartedAt, DateTime.UtcNow.AddMinutes(-3)));
        Assert.True(await secondProcessor.ProcessNextAsync(CancellationToken.None));
        var afterRetry = await secondDb.DeviceMessageInbox.AsNoTracking().SingleAsync();
        Assert.Equal("Retry", afterRetry.Status);
        Assert.NotNull(afterRetry.NextAttemptAt);

        handler.ReleaseFirstAttempt.TrySetResult();
        Assert.True(await firstAttempt);

        var saved = await secondDb.DeviceMessageInbox.AsNoTracking().SingleAsync();
        Assert.Equal("Retry", saved.Status);
        Assert.Equal(2, saved.Attempts);
        Assert.Null(saved.ProcessingStartedAt);
        Assert.NotNull(saved.NextAttemptAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Processor_PostgresOldLeaseResultCannotOverwriteNewerAttempt(bool firstAttemptFails)
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste de lease MDB exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var databaseName = $"vendmachines_inbox_lease_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(configuredConnection) { Database = "postgres" };
        var databaseBuilder = new NpgsqlConnectionStringBuilder(configuredConnection) { Database = databaseName };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var createDatabase = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await createDatabase.ExecuteNonQueryAsync();
        }

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(databaseBuilder.ConnectionString)
                .Options;
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var message = new InboundDeviceMessage($"machine-{Guid.NewGuid():N}", Guid.NewGuid(), "SERIAL-PG-LEASE",
                    "connection", 1, "{\"command\":\"pool\",\"type\":\"begin_session\"}", DateTime.UtcNow);
                setup.DeviceMessageInbox.Add(new VendingMachines.Api.Models.DeviceMessageInbox
                {
                    IdempotencyKey = Guid.NewGuid().ToString("N"),
                    MachineId = message.MachineId,
                    CompanyId = message.CompanyId,
                    Serial = message.Serial,
                    ConnectionId = message.ConnectionId,
                    MessageId = message.MessageId,
                    DataJson = message.DataJson,
                    ReceivedAt = message.ReceivedAt,
                    Status = "Pending"
                });
                await setup.SaveChangesAsync();
            }

            await using var firstDb = new AppDbContext(options);
            await using var secondDb = new AppDbContext(options);
            var handler = new LeaseRaceHandler(firstAttemptFails);
            var firstProcessor = new DeviceMessageInboxProcessor(
                firstDb, handler, NullLogger<DeviceMessageInboxProcessor>.Instance);
            var secondProcessor = new DeviceMessageInboxProcessor(
                secondDb, handler, NullLogger<DeviceMessageInboxProcessor>.Instance);

            var firstAttempt = firstProcessor.ProcessNextAsync(CancellationToken.None);
            await handler.FirstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await secondDb.DeviceMessageInbox.ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.ProcessingStartedAt, DateTime.UtcNow.AddMinutes(-3)));

            Assert.True(await secondProcessor.ProcessNextAsync(CancellationToken.None));
            handler.ReleaseFirstAttempt.TrySetResult();
            Assert.True(await firstAttempt);

            var saved = await secondDb.DeviceMessageInbox.AsNoTracking().SingleAsync();
            var expectedStatus = firstAttemptFails ? "Processed" : "Retry";
            Assert.Equal(expectedStatus, saved.Status);
            Assert.Equal(2, saved.Attempts);
            Assert.Null(saved.ProcessingStartedAt);
            Assert.Equal(firstAttemptFails, saved.NextAttemptAt is null);
            Assert.Equal(firstAttemptFails ? 0 : 1,
                await secondProcessor.CountUnfinishedAsync(CancellationToken.None));
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(adminBuilder.ConnectionString);
            await cleanup.OpenAsync();
            await using var dropDatabase = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", cleanup);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task PersistBeforeAcknowledgement_AppliesConfiguredBackpressureLimit()
    {
        await using var connection = await CreateDatabaseAsync();
        await using var db = CreateContext(connection);
        var companyId = Guid.NewGuid();
        db.DeviceMessageInbox.AddRange(Enumerable.Range(0, 100).Select(index => new VendingMachines.Api.Models.DeviceMessageInbox
        {
            IdempotencyKey = index.ToString("x64"),
            MachineId = $"machine-{index}",
            CompanyId = companyId,
            Serial = $"SERIAL-{index}",
            ConnectionId = "seed",
            MessageId = index,
            Status = "Pending"
        }));
        await db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Telemetry:MaxPendingInboundMessages"] = "100" }).Build();
        var inbox = new DeviceMessageInboxService(db, configuration, new DeviceMessageInboxAdmissionGate());
        var message = new InboundDeviceMessage("overflow-machine", companyId, "SERIAL-OVERFLOW", "overflow",
            101, "{\"command\":\"vend\",\"type\":\"success\"}", DateTime.UtcNow);

        await Assert.ThrowsAsync<DeviceMessageInboxFullException>(
            () => inbox.PersistBeforeAcknowledgementAsync(message, CancellationToken.None));
        Assert.Equal(100, await db.DeviceMessageInbox.CountAsync());
    }

    [Fact]
    public async Task PostgresConcurrentAdmissionsRespectPendingLimitAcrossIndependentContexts()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "O teste de admissão concorrente exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var databaseName = $"vendmachines_inbox_capacity_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(configuredConnection) { Database = "postgres" };
        var databaseBuilder = new NpgsqlConnectionStringBuilder(configuredConnection) { Database = databaseName };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var createDatabase = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await createDatabase.ExecuteNonQueryAsync();
        }

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(databaseBuilder.ConnectionString)
                .Options;
            var incomingOwners = Enumerable.Range(0, 2).Select(index => new
            {
                MachineId = $"incoming-machine-{index}",
                CompanyId = Guid.NewGuid(),
                Serial = $"INCOMING-{index}",
                ConnectionId = $"connection-{index}",
                OwnerNodeId = $"owner-node-{index}"
            }).ToArray();
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.DeviceMessageInbox.AddRange(Enumerable.Range(0, 99).Select(index =>
                    new DeviceMessageInbox
                    {
                        IdempotencyKey = index.ToString("x64"),
                        MachineId = $"seed-machine-{index}",
                        CompanyId = Guid.NewGuid(),
                        Serial = $"SEED-{index}",
                        ConnectionId = "seed-connection",
                        MessageId = index,
                        DataJson = "{}",
                        Status = "Pending"
                    }));
                setup.Companies.AddRange(incomingOwners.Select((owner, index) => new Company
                {
                    Id = owner.CompanyId,
                    Name = $"Empresa sintética inbox {index}"
                }));
                setup.Machines.AddRange(incomingOwners.Select(owner => new Machine
                {
                    Id = owner.MachineId,
                    Name = owner.MachineId,
                    SerialNumber = owner.Serial,
                    NormalizedSerialNumber = owner.Serial,
                    CompanyId = owner.CompanyId
                }));
                setup.MachineConnectionStates.AddRange(incomingOwners.Select(owner => new MachineConnectionState
                {
                    MachineId = owner.MachineId,
                    CompanyId = owner.CompanyId,
                    IsOnline = true,
                    ConnectionId = owner.ConnectionId,
                    OwnerConnectionId = owner.ConnectionId,
                    OwnerNodeId = owner.OwnerNodeId,
                    CommandRoutingVersion = TelemetryConnectionManager.CurrentCommandRoutingVersion,
                    LastSeenAt = DateTime.UtcNow
                }));
                await setup.SaveChangesAsync();
            }

            var messages = incomingOwners.Select((owner, index) => new InboundDeviceMessage(
                owner.MachineId,
                owner.CompanyId,
                owner.Serial,
                owner.ConnectionId,
                index + 100,
                "{\"command\":\"pool\",\"type\":\"begin_session\"}",
                DateTime.UtcNow,
                owner.OwnerNodeId)).ToArray();

            async Task<bool> TryAdmitAsync(InboundDeviceMessage message)
            {
                await using var db = new AppDbContext(options);
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Telemetry:MaxPendingInboundMessages"] = "100" }).Build();
                var service = new DeviceMessageInboxService(db, configuration, new DeviceMessageInboxAdmissionGate());
                try
                {
                    await service.PersistBeforeAcknowledgementAsync(message, CancellationToken.None);
                    return true;
                }
                catch (DeviceMessageInboxFullException)
                {
                    return false;
                }
            }

            var admitted = await Task.WhenAll(messages.Select(TryAdmitAsync));

            Assert.Single(admitted, result => result);
            await using var verify = new AppDbContext(options);
            Assert.Equal(100, await verify.DeviceMessageInbox.CountAsync(row =>
                row.Status == "Pending" || row.Status == "Retry" || row.Status == "Processing"));
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(adminBuilder.ConnectionString);
            await cleanup.OpenAsync();
            await using var dropDatabase = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", cleanup);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task SqliteCompatibilityInitializer_CreatesInboxForExistingDatabase()
    {
        await using var connection = await CreateDatabaseAsync();
        await using var db = CreateContext(connection);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"DeviceMessageInbox\"");

        DbInitializer.Initialize(db, seedDemoData: false);

        Assert.True(await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = 'DeviceMessageInbox'")
            .SingleAsync() == 1);
        var newMessage = new VendingMachines.Api.Models.DeviceMessageInbox
        {
            IdempotencyKey = new string('c', 64),
            MachineId = "legacy-sqlite-machine",
            CompanyId = Guid.NewGuid(),
            Serial = "LEGACY-SERIAL",
            ConnectionId = "legacy-connection",
            MessageId = 1,
            DataJson = "{}"
        };
        db.DeviceMessageInbox.Add(newMessage);
        await db.SaveChangesAsync();
        Assert.Equal("Pending", (await db.DeviceMessageInbox.AsNoTracking().SingleAsync()).Status);
    }

    private static async Task<SqliteConnection> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        return connection;
    }

    private static AppDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

    private static DeviceMessageInboxService CreateInboxService(AppDbContext db) =>
        new(db, new ConfigurationBuilder().Build(), new DeviceMessageInboxAdmissionGate());

    private sealed class RecordingHandler(Action<InboundDeviceMessage>? callback = null) : IDeviceTelemetryMessageHandler
    {
        public List<InboundDeviceMessage> Messages { get; } = [];

        public Task HandleDeviceMessageAsync(InboundDeviceMessage message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            callback?.Invoke(message);
            return Task.CompletedTask;
        }
    }

    private sealed class LeaseRaceHandler(bool firstAttemptFails) : IDeviceTelemetryMessageHandler
    {
        private int _calls;
        public TaskCompletionSource FirstAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstAttempt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task HandleDeviceMessageAsync(InboundDeviceMessage message, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) != 1)
            {
                if (!firstAttemptFails)
                    throw new InvalidOperationException("Falha na retomada mais recente.");
                return;
            }

            FirstAttemptStarted.TrySetResult();
            await ReleaseFirstAttempt.Task.WaitAsync(cancellationToken);
            if (firstAttemptFails)
                throw new InvalidOperationException("Falha tardia da tentativa cujo lease expirou.");

            return;
        }
    }
}
