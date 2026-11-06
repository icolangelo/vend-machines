using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class TelemetryCommandCancellationAuditTests
{
    [Fact]
    public async Task PersistsAmbiguousCommandAuditEvenWhenRequestIsCancelled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.Companies.Add(new Company { Id = companyId, Name = "Empresa de teste" });
        db.Machines.Add(new Machine
        {
            Id = "machine-1",
            Name = "Máquina de teste",
            SerialNumber = "SERIAL-1",
            NormalizedSerialNumber = "SERIAL-1",
            CompanyId = companyId
        });
        db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = "machine-1",
            CompanyId = companyId,
            MonitoringEnabled = true
        });
        db.SessionAdmissionGates.Add(new SessionAdmissionGate
        {
            Id = SessionAdmissionGate.SingletonId,
            NewSessionsPaused = false,
            UpdatedBy = "test",
            Reason = "command cancellation audit fixture"
        });
        await db.SaveChangesAsync();

        using var requestCancellation = new CancellationTokenSource();
        var controller = new TelemetryController(
            db,
            null!,
            null!,
            null!,
            null!,
            null!,
            Options.Create(new MdbStatusOptions()),
            new CancellingCommandSender(requestCancellation))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("company_id", companyId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString())
                    }, "Test"))
                }
            }
        };

        var response = await controller.SendCommand(
            "machine-1",
            new SendCommandRequest { Command = "TEST_COMMAND" },
            requestCancellation.Token);

        var error = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, error.StatusCode);
        var command = await db.TelemetryCommands.SingleAsync();
        Assert.Equal("failed", command.Status);
        Assert.Equal("command_outcome_unknown", command.Error);
        Assert.Equal(userId, command.UserId);
        Assert.True(requestCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task PostgresReleasePauseWaitsForCancelledCommandAuditToCommit()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste de pausa concorrente exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var databaseName = $"vendmachines_cancelled_command_{Guid.NewGuid():N}";
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
            var companyId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Companies.Add(new Company { Id = companyId, Name = "Empresa de teste" });
                setup.Machines.Add(new Machine
                {
                    Id = "machine-1",
                    Name = "Máquina de teste",
                    SerialNumber = "SERIAL-1",
                    NormalizedSerialNumber = "SERIAL-1",
                    CompanyId = companyId
                });
                setup.MachineConnectionStates.Add(new MachineConnectionState
                {
                    MachineId = "machine-1",
                    CompanyId = companyId,
                    MonitoringEnabled = true
                });
                setup.SessionAdmissionGates.Add(new SessionAdmissionGate
                {
                    Id = SessionAdmissionGate.SingletonId,
                    NewSessionsPaused = false,
                    UpdatedBy = "test",
                    Reason = "cancelled command audit fixture"
                });
                await setup.SaveChangesAsync();
            }

            using var requestCancellation = new CancellationTokenSource();
            var sender = new BlockingCancellingCommandSender(requestCancellation);
            await using var commandDb = new AppDbContext(options);
            var controller = new TelemetryController(
                commandDb,
                null!,
                null!,
                null!,
                null!,
                null!,
                Options.Create(new MdbStatusOptions()),
                sender)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim("company_id", companyId.ToString()),
                            new Claim(ClaimTypes.NameIdentifier, userId.ToString())
                        }, "Test"))
                    }
                }
            };

            var commandTask = controller.SendCommand(
                "machine-1",
                new SendCommandRequest { Command = "TEST_COMMAND" },
                requestCancellation.Token);
            await sender.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await using var pauseDb = new AppDbContext(options);
            await pauseDb.Database.OpenConnectionAsync();
            var pauseConnection = (NpgsqlConnection)pauseDb.Database.GetDbConnection();
            var pauseBackendPid = pauseConnection.ProcessID;
            var pauseTask = pauseDb.SessionAdmissionGates
                .Where(gate => gate.Id == SessionAdmissionGate.SingletonId)
                .ExecuteUpdateAsync(update => update.SetProperty(gate => gate.NewSessionsPaused, true));
            await using var observer = new NpgsqlConnection(databaseBuilder.ConnectionString);
            await observer.OpenAsync();
            var lockWaitDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            var pauseIsWaitingOnLock = false;
            while (DateTime.UtcNow < lockWaitDeadline && !pauseTask.IsCompleted)
            {
                await using var checkLockWait = new NpgsqlCommand(
                    "SELECT wait_event_type = 'Lock' FROM pg_stat_activity WHERE pid = @pid",
                    observer);
                checkLockWait.Parameters.AddWithValue("pid", pauseBackendPid);
                pauseIsWaitingOnLock = await checkLockWait.ExecuteScalarAsync() as bool? == true;
                if (pauseIsWaitingOnLock) break;
                await Task.Delay(TimeSpan.FromMilliseconds(25));
            }
            Assert.True(pauseIsWaitingOnLock, "O UPDATE da pausa não foi observado aguardando o lock da admissão do comando.");
            Assert.False(pauseTask.IsCompleted);

            requestCancellation.Cancel();
            sender.Complete.TrySetResult();
            var response = await commandTask.WaitAsync(TimeSpan.FromSeconds(5));
            var error = Assert.IsType<ObjectResult>(response);
            Assert.Equal(StatusCodes.Status504GatewayTimeout, error.StatusCode);
            await pauseTask.WaitAsync(TimeSpan.FromSeconds(5));

            var audit = await pauseDb.TelemetryCommands.AsNoTracking().SingleAsync();
            Assert.Equal("command_outcome_unknown", audit.Error);
            Assert.True(await pauseDb.SessionAdmissionGates.AsNoTracking()
                .Where(gate => gate.Id == SessionAdmissionGate.SingletonId)
                .Select(gate => gate.NewSessionsPaused)
                .SingleAsync());
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var dropDatabase = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task CancelledRemoteCommandIsRemovedBeforeDispatchAndDoesNotBlockReleaseReadiness()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste de cancelamento de dispatch remoto exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var databaseName = $"vendmachines_cancelled_remote_command_{Guid.NewGuid():N}";
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
            var companyId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            const string machineId = "machine-cancelled-remote-command";
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Companies.Add(new Company { Id = companyId, Name = "Empresa sintética" });
                setup.Machines.Add(new Machine
                {
                    Id = machineId,
                    Name = "Máquina sintética",
                    SerialNumber = "SERIAL-CANCELLED-REMOTE",
                    NormalizedSerialNumber = "SERIAL-CANCELLED-REMOTE",
                    CompanyId = companyId
                });
                setup.MachineConnectionStates.Add(new MachineConnectionState
                {
                    MachineId = machineId,
                    CompanyId = companyId,
                    MonitoringEnabled = true,
                    IsOnline = true,
                    LastSeenAt = DateTime.UtcNow,
                    ConnectionId = "synthetic-owner-connection",
                    OwnerNodeId = "synthetic-owner-node",
                    OwnerConnectionId = "synthetic-owner-connection",
                    CommandRoutingVersion = TelemetryConnectionManager.CurrentCommandRoutingVersion,
                    OwnerLeaseExpiresAt = DateTime.UtcNow.AddMinutes(1)
                });
                setup.SessionAdmissionGates.Add(new SessionAdmissionGate
                {
                    Id = SessionAdmissionGate.SingletonId,
                    NewSessionsPaused = false,
                    UpdatedBy = "test",
                    Reason = "cancelled remote command fixture"
                });
                setup.SystemSettings.Add(new SystemSettings { ApplicationFeePercent = 5m });
                await setup.SaveChangesAsync();
            }

            await using var services = new ServiceCollection()
                .AddDbContext<AppDbContext>(builder => builder.UseNpgsql(databaseBuilder.ConnectionString))
                .BuildServiceProvider();
            using var commandManager = new TelemetryConnectionManager(
                services.GetRequiredService<IServiceScopeFactory>(),
                new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance),
                NullLogger<TelemetryConnectionManager>.Instance);
            using var requestCancellation = new CancellationTokenSource();
            await using var controllerDb = new AppDbContext(options);
            var controller = new TelemetryController(
                controllerDb,
                null!,
                null!,
                null!,
                null!,
                null!,
                Options.Create(new MdbStatusOptions()),
                commandManager)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim("company_id", companyId.ToString()),
                            new Claim(ClaimTypes.NameIdentifier, userId.ToString())
                        }, "Test"))
                    }
                }
            };

            var commandTask = controller.SendCommand(
                machineId,
                new SendCommandRequest { Command = "TEST_REMOTE_COMMAND" },
                requestCancellation.Token);
            TelemetryCommandDispatch? pendingDispatch = null;
            var dispatchDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < dispatchDeadline)
            {
                await using var pollDb = new AppDbContext(options);
                pendingDispatch = await pollDb.TelemetryCommandDispatches.AsNoTracking()
                    .SingleOrDefaultAsync(dispatch => dispatch.MachineId == machineId);
                if (pendingDispatch != null) break;
                await Task.Delay(TimeSpan.FromMilliseconds(25));
            }
            if (pendingDispatch is null)
            {
                requestCancellation.Cancel();
                var missingDispatchResult = await commandTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Fail($"O comando terminou sem persistir dispatch remoto; resposta: {missingDispatchResult.GetType().Name}.");
            }
            Assert.Equal("Pending", pendingDispatch.Status);

            requestCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await commandTask.WaitAsync(TimeSpan.FromSeconds(5)));

            await using var verificationDb = new AppDbContext(options);
            var persistedDispatch = await verificationDb.TelemetryCommandDispatches.AsNoTracking().SingleAsync();
            Assert.Equal("Cancelled", persistedDispatch.Status);
            Assert.Empty(await verificationDb.TelemetryCommands.AsNoTracking().ToListAsync());
            await verificationDb.SessionAdmissionGates
                .Where(gate => gate.Id == SessionAdmissionGate.SingletonId)
                .ExecuteUpdateAsync(update => update.SetProperty(gate => gate.NewSessionsPaused, true));

            var readinessBlocker = await new DeploymentReadinessService(verificationDb).GetBlockerAsync(CancellationToken.None);
            Assert.True(readinessBlocker is null, $"Bloqueador inesperado após cancelar o dispatch: {readinessBlocker}");
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

    private sealed class CancellingCommandSender(CancellationTokenSource cancellation) : ITelemetryCommandSender
    {
        public Task<DeviceCommandResult> SendCommandAsync(
            string serial,
            object data,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.FromResult(new DeviceCommandResult(false, 42, 1, "command_outcome_unknown"));
        }
    }

    private sealed class BlockingCancellingCommandSender(CancellationTokenSource cancellation) : ITelemetryCommandSender
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DeviceCommandResult> SendCommandAsync(
            string serial,
            object data,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Complete.Task;
            cancellation.Cancel();
            return new DeviceCommandResult(false, 42, 1, "command_outcome_unknown");
        }
    }
}
