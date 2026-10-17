using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class TelemetryHandshakeIntegrationTests
{
    [Fact]
    public async Task AuthenticatedReconnectRepairsStateOwnedByAnotherCompany()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: true, revokedAt: null);
        var machine = await scenario.Db.Machines.SingleAsync();
        var ownerCompanyId = machine.CompanyId!.Value;
        var previousCompanyId = Guid.NewGuid();
        scenario.Db.Companies.Add(new Company
        {
            Id = previousCompanyId,
            Name = "Empresa anterior",
            Cnpj = "55555555000155"
        });
        scenario.Db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = machine.Id,
            CompanyId = previousCompanyId,
            MonitoringEnabled = true,
            MdbStatus = MdbStatuses.Vend,
            MdbStatusRaw = "{\"status\":\"vend_state\"}",
            MdbStatusUpdatedAt = DateTime.UtcNow,
            AutoOpenLastError = "erro de outra empresa"
        });
        await scenario.Db.SaveChangesAsync();

        using var socket = new ScriptedWebSocket();
        socket.SendText(StartupFrame(scenario.Credential));
        var handshake = scenario.Manager.HandleDeviceAsync(socket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        MachineConnectionState state;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            state = await scenario.Db.MachineConnectionStates.AsNoTracking().SingleAsync();
            if (state.CompanyId == ownerCompanyId && state.IsOnline) break;
            await Task.Delay(20);
        } while (DateTime.UtcNow < deadline);

        Assert.Equal(ownerCompanyId, state.CompanyId);
        Assert.True(state.IsOnline);
        Assert.False(state.MonitoringEnabled);
        Assert.Null(state.MdbStatus);
        Assert.Null(state.MdbStatusRaw);
        Assert.Null(state.MdbStatusUpdatedAt);
        Assert.Null(state.AutoOpenLastError);

        socket.SendClose();
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ValidCredentialCompletesStartHandshakeAndAcknowledgesMachine()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: true, revokedAt: null);
        using var socket = new ScriptedWebSocket();
        socket.SendText(StartupFrame(scenario.Credential));
        var handshake = scenario.Manager.HandleDeviceAsync(socket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        socket.SendClose();

        await handshake;

        Assert.Contains(socket.SentMessages, message => message.Contains("\"type\":\"ack\"", StringComparison.Ordinal));
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
        var state = await scenario.Db.MachineConnectionStates.SingleAsync();
        Assert.False(state.IsOnline);
        Assert.Null(state.ConnectionId);
        var connectionEvents = await scenario.Db.TelemetryEvents.Select(x => x.EventType).ToListAsync();
        Assert.Equal(2, connectionEvents.Count);
        Assert.Contains("device.connected", connectionEvents);
        Assert.Contains("device.disconnected", connectionEvents);
    }

    [Fact]
    public async Task RevokedCredentialIsRejectedBeforeMachineIsRegistered()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: true, revokedAt: DateTime.UtcNow);
        using var socket = new ScriptedWebSocket();
        socket.SendText(StartupFrame(scenario.Credential));

        await scenario.Manager.HandleDeviceAsync(socket, CancellationToken.None);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
        Assert.DoesNotContain(socket.SentMessages, message => message.Contains("\"type\":\"ack\"", StringComparison.Ordinal));
        Assert.Empty(await scenario.Db.MachineConnectionStates.ToListAsync());
    }

    [Fact]
    public async Task OptionalCredentialModeAuthenticatesProvidedCredentialsBeforeFleetEnforcement()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: false, revokedAt: null);

        using var invalidSocket = new ScriptedWebSocket();
        invalidSocket.SendText(StartupFrame("vmc_invalid"));
        await scenario.Manager.HandleDeviceAsync(invalidSocket, CancellationToken.None);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, invalidSocket.CloseStatus);
        Assert.Empty(await scenario.Db.MachineConnectionStates.ToListAsync());

        using var credentialedSocket = new ScriptedWebSocket();
        credentialedSocket.SendText(StartupFrame(scenario.Credential));
        var handshake = scenario.Manager.HandleDeviceAsync(credentialedSocket, CancellationToken.None);
        await credentialedSocket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        credentialedSocket.SendClose();
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(credentialedSocket.SentMessages, message => message.Contains("\"type\":\"ack\"", StringComparison.Ordinal));
        Assert.Equal(WebSocketCloseStatus.NormalClosure, credentialedSocket.CloseStatus);
    }

    [Fact]
    public async Task LegacyDuplicateHandshakeIsRejectedAndLeavesExistingSocketOnline()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: false, revokedAt: null);
        using var existingSocket = new ScriptedWebSocket();
        existingSocket.SendText(StartupFrame(credential: null));
        var existingHandshake = scenario.Manager.HandleDeviceAsync(existingSocket, CancellationToken.None);
        await existingSocket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var duplicateSocket = new ScriptedWebSocket();
        duplicateSocket.SendText(StartupFrame(credential: null));
        await scenario.Manager.HandleDeviceAsync(duplicateSocket, CancellationToken.None);

        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, duplicateSocket.CloseStatus);
        Assert.True(scenario.Manager.IsOnline("SERIAL-1"));
        Assert.False(existingSocket.WasAborted);
        var state = await scenario.Db.MachineConnectionStates.SingleAsync();
        Assert.True(state.IsOnline);
        Assert.Equal(scenario.Manager.NodeId, state.OwnerNodeId);
        Assert.Equal(state.ConnectionId, state.OwnerConnectionId);

        existingSocket.SendClose();
        await existingHandshake.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ShutdownRejectsNewCommandsAndClosesConnectedMachineForReconnect()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: false, revokedAt: null);
        using var socket = new ScriptedWebSocket();
        socket.SendText(StartupFrame(credential: null));
        var handshake = scenario.Manager.HandleDeviceAsync(socket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var shutdown = scenario.Manager.StopAsync(CancellationToken.None);
        var command = await scenario.Manager.SendCommandAsync("SERIAL-1", "MDB_STATUS");

        using var reconnectDuringShutdown = new ScriptedWebSocket();
        reconnectDuringShutdown.SendText(StartupFrame(credential: null));
        await scenario.Manager.HandleDeviceAsync(reconnectDuringShutdown, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(command.Success);
        Assert.Equal("server_restarting", command.Error);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, reconnectDuringShutdown.CloseStatus);
        Assert.Empty(reconnectDuringShutdown.SentMessages);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, socket.CloseStatus);
        Assert.Contains("Reconecte", socket.CloseStatusDescription);
        Assert.False(scenario.Manager.IsOnline("SERIAL-1"));
        var state = await scenario.Db.MachineConnectionStates.SingleAsync();
        Assert.False(state.IsOnline);
        Assert.Null(state.ConnectionId);
        Assert.Null(state.OwnerNodeId);
        Assert.Null(state.OwnerConnectionId);
    }

    [Fact]
    public async Task ApplicationStoppingDrainsMachineSocketBeforeHostStopsRequests()
    {
        var lifetime = new TestHostApplicationLifetime();
        await using var scenario = await CreateScenarioAsync(requireCredentials: false, revokedAt: null, lifetime);
        using var socket = new ScriptedWebSocket();
        socket.SendText(StartupFrame(credential: null));
        var handshake = scenario.Manager.HandleDeviceAsync(socket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lifetime.StopApplication();
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, socket.CloseStatus);
        Assert.Contains("Reconecte", socket.CloseStatusDescription);
        Assert.False(scenario.Manager.IsOnline("SERIAL-1"));
        var state = await scenario.Db.MachineConnectionStates.SingleAsync();
        Assert.False(state.IsOnline);
        Assert.Null(state.ConnectionId);
        Assert.Null(state.OwnerNodeId);
        Assert.Null(state.OwnerConnectionId);
        Assert.Equal("server_restarting", (await scenario.Manager.SendCommandAsync("SERIAL-1", "MDB_STATUS")).Error);
    }

    [Fact]
    public async Task CommandFromAnotherNodeIsSentOnceByTheSocketOwner()
    {
        var connectionString = $"Data Source=telemetry-dispatch-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var databaseKeeper = new SqliteConnection(connectionString);
        await databaseKeeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var company = new Company { Id = Guid.NewGuid(), Name = "Empresa de roteamento", ActivationStatus = CompanyActivationStatuses.Active };
            db.Companies.Add(company);
            db.Machines.Add(new Machine
            {
                Id = "machine-route", Name = "Máquina", SerialNumber = "SERIAL-ROUTE",
                NormalizedSerialNumber = "SERIAL-ROUTE", CompanyId = company.Id,
                TelemetryCredentialHash = DeviceCredentialService.Hash(DeviceCredentialService.Generate())
            });
            await db.SaveChangesAsync();
        }

        await using var owner = CreateNode(connectionString);
        await using var caller = CreateNode(connectionString);
        await owner.Manager.StartAsync(CancellationToken.None);
        await caller.Manager.StartAsync(CancellationToken.None);

        using var socket = new ScriptedWebSocket();
        socket.SendText("{\"id\":1,\"type\":\"start\",\"target\":\"SERIAL-ROUTE\"}");
        var handshake = owner.Manager.HandleDeviceAsync(socket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var send = caller.Manager.SendCommandAsync("SERIAL-ROUTE", "MDB_STATUS");
        string? commandFrame = null;
        var commandDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < commandDeadline)
        {
            commandFrame = socket.SentMessages.FirstOrDefault(message => message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal));
            if (commandFrame != null) break;
            await Task.Delay(25);
        }

        Assert.NotNull(commandFrame);
        using (var command = JsonDocument.Parse(commandFrame!))
            socket.SendText($"{{\"type\":\"ack\",\"id\":{command.RootElement.GetProperty("id").GetInt32()}}}");

        var result = await send.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success);
        Assert.Equal(1, socket.SentMessages.Count(message => message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal)));

        Assert.True(await caller.Manager.IsOnlineAsync("SERIAL-ROUTE"));
        Assert.Contains("SERIAL-ROUTE", await caller.Manager.GetOnlineSerialsAsync(["SERIAL-ROUTE"]));
        var previousStatusCommandCount = socket.SentMessages.Count(message =>
            message.Contains("\"type\":\"msg\"", StringComparison.Ordinal) &&
            message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal));
        var statusRequest = caller.Manager.SendStatusRequestAsync("SERIAL-ROUTE", TimeSpan.FromSeconds(5));
        string? statusFrame = null;
        var statusDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < statusDeadline)
        {
            statusFrame = socket.SentMessages.LastOrDefault(message =>
                message.Contains("\"type\":\"msg\"", StringComparison.Ordinal) &&
                message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal));
            if (statusFrame != null && socket.SentMessages.Count(message =>
                    message.Contains("\"type\":\"msg\"", StringComparison.Ordinal) &&
                    message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal)) > previousStatusCommandCount)
                break;
            await Task.Delay(25);
        }
        Assert.NotNull(statusFrame);
        using (var statusCommand = JsonDocument.Parse(statusFrame!))
        {
            var messageId = statusCommand.RootElement.GetProperty("id").GetInt32();
            socket.SendText($"{{\"id\":{messageId},\"type\":\"msg\",\"target\":\"SERIAL-ROUTE\",\"data\":{{\"command\":\"status\",\"status\":\"idle_state\"}}}}");
        }
        var statusResult = await statusRequest.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(statusResult.Success);
        Assert.Equal(MdbStatuses.Idle, statusResult.Status);

        socket.SendClose();
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));
        await owner.Manager.StopAsync(CancellationToken.None);
        await caller.Manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PostgresCommandRoutingSurvivesOwnerShutdownAndDeviceReconnect()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var databaseName = $"telemetry_dispatch_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "postgres" };
        var databaseBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = databaseName };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var createDatabase = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await createDatabase.ExecuteNonQueryAsync();
        }

        try
        {
            var scopedConnectionString = databaseBuilder.ConnectionString;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(scopedConnectionString).Options;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                var company = new Company
                {
                    Id = Guid.NewGuid(),
                    Name = "Empresa de roteamento PostgreSQL",
                    ActivationStatus = CompanyActivationStatuses.Active
                };
                db.Companies.Add(company);
                db.Machines.Add(new Machine
                {
                    Id = "machine-postgres-route",
                    Name = "Máquina PostgreSQL",
                    SerialNumber = "SERIAL-PG-ROUTE",
                    NormalizedSerialNumber = "SERIAL-PG-ROUTE",
                    CompanyId = company.Id
                });
                await db.SaveChangesAsync();
            }

            await using (var owner = CreatePostgresNode(scopedConnectionString))
            await using (var caller = CreatePostgresNode(scopedConnectionString))
            await using (var requester = CreatePostgresNode(scopedConnectionString))
            {
                await owner.Manager.StartAsync(CancellationToken.None);
                await caller.Manager.StartAsync(CancellationToken.None);
                await requester.Manager.StartAsync(CancellationToken.None);

                using var socket = new ScriptedWebSocket();
                socket.SendText("{\"id\":1,\"type\":\"start\",\"target\":\"SERIAL-PG-ROUTE\"}");
                var handshake = owner.Manager.HandleDeviceAsync(socket, CancellationToken.None);
                await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

                var routeDeadline = DateTime.UtcNow.AddSeconds(5);
                var ownerPersisted = false;
                while (DateTime.UtcNow < routeDeadline)
                {
                    await using var routeDb = new AppDbContext(options);
                    ownerPersisted = await routeDb.MachineConnectionStates.AsNoTracking().AnyAsync(state =>
                        state.MachineId == "machine-postgres-route" &&
                        state.IsOnline &&
                        state.OwnerNodeId == owner.Manager.NodeId);
                    if (ownerPersisted) break;
                    await Task.Delay(25);
                }
                Assert.True(ownerPersisted, "O socket precisa estar registrado no nó proprietário antes de testar o despacho entre nós.");

                var send = caller.Manager.SendCommandAsync("SERIAL-PG-ROUTE", "MDB_STATUS");
                string? commandFrame = null;
                var commandDeadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < commandDeadline)
                {
                    commandFrame = socket.SentMessages.FirstOrDefault(message =>
                        message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal));
                    if (commandFrame != null) break;
                    await Task.Delay(25);
                }

                Assert.NotNull(commandFrame);
                using (var command = JsonDocument.Parse(commandFrame!))
                    socket.SendText($"{{\"type\":\"ack\",\"id\":{command.RootElement.GetProperty("id").GetInt32()}}}");

                var result = await send.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(result.Success);
                Assert.Equal(1, socket.SentMessages.Count(message =>
                    message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal)));

                await owner.Manager.StopAsync(CancellationToken.None);
                await handshake.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, socket.CloseStatus);

                using (var offlineDb = new AppDbContext(options))
                {
                    var offlineState = await offlineDb.MachineConnectionStates.AsNoTracking()
                        .SingleAsync(state => state.MachineId == "machine-postgres-route");
                    Assert.False(offlineState.IsOnline);
                    Assert.Null(offlineState.OwnerNodeId);
                }

                using var reconnectedSocket = new ScriptedWebSocket();
                reconnectedSocket.SendText("{\"id\":1,\"type\":\"start\",\"target\":\"SERIAL-PG-ROUTE\"}");
                var reconnectHandshake = caller.Manager.HandleDeviceAsync(reconnectedSocket, CancellationToken.None);
                await reconnectedSocket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

                var reconnectDeadline = DateTime.UtcNow.AddSeconds(5);
                var newOwnerPersisted = false;
                while (DateTime.UtcNow < reconnectDeadline)
                {
                    await using var routeDb = new AppDbContext(options);
                    newOwnerPersisted = await routeDb.MachineConnectionStates.AsNoTracking().AnyAsync(state =>
                        state.MachineId == "machine-postgres-route" &&
                        state.IsOnline &&
                        state.OwnerNodeId == caller.Manager.NodeId);
                    if (newOwnerPersisted) break;
                    await Task.Delay(25);
                }
                Assert.True(newOwnerPersisted, "Após reconectar, a propriedade deve apontar para o novo nó antes de rotear comandos.");

                var handoffSend = requester.Manager.SendCommandAsync("SERIAL-PG-ROUTE", "MDB_STATUS");
                string? handoffFrame = null;
                var handoffDeadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < handoffDeadline)
                {
                    handoffFrame = reconnectedSocket.SentMessages.FirstOrDefault(message =>
                        message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal));
                    if (handoffFrame != null) break;
                    await Task.Delay(25);
                }

                Assert.NotNull(handoffFrame);
                using (var command = JsonDocument.Parse(handoffFrame!))
                    reconnectedSocket.SendText($"{{\"type\":\"ack\",\"id\":{command.RootElement.GetProperty("id").GetInt32()}}}");

                var handoffResult = await handoffSend.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(handoffResult.Success);
                Assert.Equal(1, reconnectedSocket.SentMessages.Count(message =>
                    message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal)));

                reconnectedSocket.SendClose();
                await reconnectHandshake.WaitAsync(TimeSpan.FromSeconds(5));
            }
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
    public async Task LegacyOnlineMachineWithoutOwnerRemainsVisibleAndReturnsExplicitUnsupportedRoute()
    {
        var connectionString = $"Data Source=telemetry-legacy-owner-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var databaseKeeper = new SqliteConnection(connectionString);
        await databaseKeeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var company = new Company { Id = Guid.NewGuid(), Name = "Empresa legada", ActivationStatus = CompanyActivationStatuses.Active };
            db.Companies.Add(company);
            db.Machines.Add(new Machine
            {
                Id = "machine-legacy-owner", Name = "Máquina legada", SerialNumber = "SERIAL-LEGACY",
                NormalizedSerialNumber = "SERIAL-LEGACY", CompanyId = company.Id
            });
            db.MachineConnectionStates.Add(new MachineConnectionState
            {
                MachineId = "machine-legacy-owner", CompanyId = company.Id, IsOnline = true,
                ConnectionId = "legacy-connection", LastSeenAt = DateTime.UtcNow, OwnerNodeId = null
            });
            await db.SaveChangesAsync();
        }

        await using var apiNode = CreateNode(connectionString);
        Assert.True(await apiNode.Manager.IsOnlineAsync("SERIAL-LEGACY"));
        Assert.Contains("SERIAL-LEGACY", await apiNode.Manager.GetOnlineSerialsAsync(["SERIAL-LEGACY"]));

        var command = await apiNode.Manager.SendCommandAsync("SERIAL-LEGACY", "FECHAR_SESSAO");
        var status = await apiNode.Manager.SendStatusRequestAsync("SERIAL-LEGACY", TimeSpan.FromSeconds(1));
        Assert.Equal("machine_owner_unknown", command.Error);
        Assert.Equal("machine_owner_unknown", status.Error);
        Assert.False(command.Success);
        Assert.False(status.Success);
    }

    private static TelemetryNode CreateNode(string connectionString)
        => CreateNode(options => options.UseSqlite(connectionString));

    private static TelemetryNode CreatePostgresNode(string connectionString)
        => CreateNode(options => options.UseNpgsql(connectionString));

    private static TelemetryNode CreateNode(Action<DbContextOptionsBuilder> configureDatabase)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddDbContext<AppDbContext>(configureDatabase)
            .AddSingleton<DeviceMessageInboxAdmissionGate>()
            .AddScoped<DeviceMessageInboxService>()
            .AddScoped<IDeviceTelemetryMessageHandler, TestDeviceTelemetryMessageHandler>()
            .AddScoped<DeviceMessageInboxProcessor>()
            .AddSingleton(sp => new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance))
            .AddSingleton(sp => new TelemetryConnectionManager(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<TelemetryPanelHub>(),
                NullLogger<TelemetryConnectionManager>.Instance))
            .BuildServiceProvider();
        return new TelemetryNode(services, services.GetRequiredService<TelemetryConnectionManager>());
    }

    private sealed class TestDeviceTelemetryMessageHandler : IDeviceTelemetryMessageHandler
    {
        public Task HandleDeviceMessageAsync(InboundDeviceMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static string StartupFrame(string? credential)
    {
        var credentialPart = credential == null ? string.Empty : $",\"credential\":\"{credential}\"";
        return $"{{\"id\":1,\"type\":\"start\",\"target\":\"SERIAL-1\"{credentialPart}}}";
    }

    private static async Task<TelemetryScenario> CreateScenarioAsync(
        bool requireCredentials,
        DateTime? revokedAt,
        IHostApplicationLifetime? lifetime = null)
    {
        var connectionString = $"Data Source=telemetry-handshake-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa de teste", ActivationStatus = CompanyActivationStatuses.Active };
        var credential = DeviceCredentialService.Generate();
        var machine = new Machine
        {
            Id = "machine-1", Name = "Máquina de teste", SerialNumber = "SERIAL-1",
            NormalizedSerialNumber = "SERIAL-1", CompanyId = company.Id, Company = company,
            TelemetryCredentialHash = DeviceCredentialService.Hash(credential),
            TelemetryCredentialRevokedAt = revokedAt
        };
        db.Companies.Add(company);
        db.Machines.Add(machine);
        await db.SaveChangesAsync();

        var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(builder => builder.UseSqlite(connectionString))
            .BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:RequireDeviceCredentials"] = requireCredentials.ToString()
            })
            .Build();
        var manager = new TelemetryConnectionManager(
            services.GetRequiredService<IServiceScopeFactory>(),
            new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance),
            NullLogger<TelemetryConnectionManager>.Instance,
            configuration,
            lifetime);

        return new TelemetryScenario(connection, db, services, manager, credential);
    }

    private sealed class TelemetryScenario(
        SqliteConnection connection,
        AppDbContext db,
        ServiceProvider services,
        TelemetryConnectionManager manager,
        string credential) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public TelemetryConnectionManager Manager { get; } = manager;
        public string Credential { get; } = credential;

        public async ValueTask DisposeAsync()
        {
            Manager.Dispose();
            services.Dispose();
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TelemetryNode(ServiceProvider services, TelemetryConnectionManager manager) : IAsyncDisposable
    {
        public TelemetryConnectionManager Manager { get; } = manager;
        public async ValueTask DisposeAsync()
        {
            try { await Manager.StopAsync(CancellationToken.None); }
            catch { }
            await services.DisposeAsync();
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class ScriptedWebSocket : WebSocket
    {
        private readonly Channel<(byte[] Payload, WebSocketMessageType Type)> _incoming =
            Channel.CreateUnbounded<(byte[] Payload, WebSocketMessageType Type)>();
        private readonly object _sentLock = new();
        private readonly List<string> _sentMessages = [];
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;

        public TaskCompletionSource FirstTextSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<string> SentMessages
        {
            get { lock (_sentLock) return _sentMessages.ToArray(); }
        }
        public bool WasAborted { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeStatusDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void SendText(string message) => _incoming.Writer.TryWrite((Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text));
        public void SendClose() => _incoming.Writer.TryWrite((Array.Empty<byte>(), WebSocketMessageType.Close));

        public override void Abort()
        {
            WasAborted = true;
            _state = WebSocketState.Aborted;
        }

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            _incoming.Writer.TryWrite((Array.Empty<byte>(), WebSocketMessageType.Close));
            return Task.CompletedTask;
        }

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var frame = await _incoming.Reader.ReadAsync(cancellationToken);
            if (frame.Type == WebSocketMessageType.Close)
            {
                _state = WebSocketState.CloseReceived;
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            frame.Payload.AsSpan().CopyTo(buffer.Span);
            return new ValueWebSocketReceiveResult(frame.Payload.Length, frame.Type, true);
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var frame = await _incoming.Reader.ReadAsync(cancellationToken);
            if (frame.Type == WebSocketMessageType.Close)
            {
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, CloseStatus, CloseStatusDescription);
            }

            frame.Payload.AsSpan().CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(frame.Payload.Length, frame.Type, true);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            RecordSent(buffer.AsSpan(), messageType);
            return Task.CompletedTask;
        }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            RecordSent(buffer.Span, messageType);
            return ValueTask.CompletedTask;
        }

        private void RecordSent(ReadOnlySpan<byte> buffer, WebSocketMessageType messageType)
        {
            if (messageType != WebSocketMessageType.Text) return;
            lock (_sentLock) _sentMessages.Add(Encoding.UTF8.GetString(buffer));
            FirstTextSent.TrySetResult();
        }
    }
}
