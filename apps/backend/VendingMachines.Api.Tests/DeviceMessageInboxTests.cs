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
                await setup.SaveChangesAsync();
            }

            var messages = Enumerable.Range(0, 2).Select(index => new InboundDeviceMessage(
                $"incoming-machine-{index}", Guid.NewGuid(), $"INCOMING-{index}", $"connection-{index}",
                index + 100, "{\"command\":\"pool\",\"type\":\"begin_session\"}", DateTime.UtcNow)).ToArray();

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
}
