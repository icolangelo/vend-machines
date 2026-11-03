using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class TelemetryCommandAdmissionTests
{
    [Fact]
    public async Task PauseWaitsForAdmittedManualCommandToFinish()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste de admissão de comandos exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var databaseName = $"vendmachines_command_admission_{Guid.NewGuid():N}";
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
            await using var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var company = new Company { Id = Guid.NewGuid(), Name = "Empresa sintética" };
            var machine = new Machine
            {
                Id = "MACHINE-COMMAND-ADMISSION",
                Name = "Máquina sintética",
                SerialNumber = "SERIAL-COMMAND-ADMISSION",
                NormalizedSerialNumber = "SERIAL-COMMAND-ADMISSION",
                CompanyId = company.Id
            };
            db.Companies.Add(company);
            db.Machines.Add(machine);
            db.MachineConnectionStates.Add(new MachineConnectionState
            {
                MachineId = machine.Id,
                CompanyId = company.Id,
                MonitoringEnabled = true,
                IsOnline = true,
                LastSeenAt = DateTime.UtcNow
            });
            db.SessionAdmissionGates.Add(new SessionAdmissionGate
            {
                Id = SessionAdmissionGate.SingletonId,
                NewSessionsPaused = false,
                UpdatedBy = "test",
                Reason = "command admission fixture"
            });
            await db.SaveChangesAsync();

            var sender = new BlockingTelemetryCommandSender();
            var controller = new TelemetryController(
                db,
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
                            new Claim("company_id", company.Id.ToString()),
                            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
                        }, "Test"))
                    }
                }
            };

            var commandTask = controller.SendCommand(
                machine.Id,
                new SendCommandRequest { Command = "TEST" },
                CancellationToken.None);
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

            sender.Complete.TrySetResult(new DeviceCommandResult(true, 17, 1));
            var response = await commandTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsType<AcceptedResult>(response);
            await pauseTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(await pauseDb.SessionAdmissionGates
                .AsNoTracking()
                .Where(gate => gate.Id == SessionAdmissionGate.SingletonId)
                .Select(gate => gate.NewSessionsPaused)
                .SingleAsync());
            var recordedCommand = await pauseDb.TelemetryCommands.AsNoTracking().SingleAsync();
            Assert.Equal("acknowledged", recordedCommand.Status);
            Assert.Equal(17, recordedCommand.MessageId);
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

    private sealed class BlockingTelemetryCommandSender : ITelemetryCommandSender
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DeviceCommandResult> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DeviceCommandResult> SendCommandAsync(
            string serial,
            object data,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return await Complete.Task.WaitAsync(cancellationToken);
        }
    }
}
