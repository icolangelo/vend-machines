using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class TelemetryControllerIsolationTests
{
    [Fact]
    public async Task ConnectionsDoNotExposeForeignCompanyStateOrSessionForOwnedMachine()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var companyA = new Company { Id = Guid.NewGuid(), Name = "Empresa A" };
        var companyB = new Company { Id = Guid.NewGuid(), Name = "Empresa B" };
        var machine = new Machine
        {
            Id = "MACHINE-A",
            Name = "Máquina A",
            SerialNumber = "SERIAL-A",
            NormalizedSerialNumber = "SERIAL-A",
            CompanyId = companyA.Id
        };
        db.Companies.AddRange(companyA, companyB);
        db.Machines.Add(machine);
        db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = machine.Id,
            CompanyId = companyB.Id,
            MonitoringEnabled = true,
            IsOnline = true,
            LastSeenAt = DateTime.UtcNow,
            MdbStatus = MdbStatuses.Enabled,
            MdbStatusUpdatedAt = DateTime.UtcNow
        });
        db.MachineSessions.Add(new MachineSession
        {
            Id = Guid.NewGuid(),
            MachineId = machine.Id,
            CompanyId = companyB.Id,
            State = MachineSessionStates.PaymentPending,
            AmountCents = 12345,
            StartedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(builder => builder.UseSqlite(connection))
            .BuildServiceProvider();
        var connectionManager = new TelemetryConnectionManager(
            services.GetRequiredService<IServiceScopeFactory>(),
            new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance),
            NullLogger<TelemetryConnectionManager>.Instance);
        var controller = new TelemetryController(
            db,
            null!,
            connectionManager,
            null!,
            null!,
            null!,
            Options.Create(new MdbStatusOptions()));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("company_id", companyA.Id.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
                }, "Test"))
            }
        };

        try
        {
            Assert.False(await connectionManager.IsOnlineAsync(machine.SerialNumber, CancellationToken.None));
            var response = await controller.GetConnections(cancellationToken: CancellationToken.None);
            var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(response).Value));
            var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());

            Assert.False(item.GetProperty("online").GetBoolean());
            Assert.False(item.GetProperty("monitoringEnabled").GetBoolean());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("activeSession").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("mdbStatus").ValueKind);

            var setMonitoring = await controller.SetMonitoring(
                machine.Id,
                new MonitoringRequest { Enabled = false },
                CancellationToken.None);
            Assert.IsType<ConflictObjectResult>(setMonitoring);
            Assert.Equal(companyB.Id, (await db.MachineConnectionStates.SingleAsync()).CompanyId);

            var sendCommand = await controller.SendCommand(
                machine.Id,
                new SendCommandRequest { Command = "TEST" },
                CancellationToken.None);
            var commandConflict = Assert.IsType<ConflictObjectResult>(sendCommand);
            Assert.Contains("monitoring_inactive", JsonSerializer.Serialize(commandConflict.Value));
            Assert.Empty(await db.TelemetryCommands.ToListAsync());
        }
        finally
        {
            connectionManager.Dispose();
            await services.DisposeAsync();
        }
    }
}
