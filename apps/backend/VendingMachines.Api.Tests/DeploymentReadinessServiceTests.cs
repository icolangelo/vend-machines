using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class DeploymentReadinessServiceTests
{
    [Fact]
    public async Task DeploymentPreflightBlocksOpenSalesAndUnresolvedMachineCommands()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var company = new Company { Name = "Empresa de teste", Cnpj = "11222333000181" };
        var machine = new Machine
        {
            Id = "preflight-machine", Name = "Máquina de teste", SerialNumber = "PREFLIGHT-1",
            NormalizedSerialNumber = "PREFLIGHT-1", CompanyId = company.Id
        };
        db.Companies.Add(company);
        db.Machines.Add(machine);
        await db.SaveChangesAsync();

        var readiness = new DeploymentReadinessService(db);
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));

        var session = new MachineSession
        {
            MachineId = machine.Id,
            CompanyId = company.Id,
            State = MachineSessionStates.AwaitingSelection
        };
        db.MachineSessions.Add(session);
        await db.SaveChangesAsync();
        Assert.Equal("active_vend_sessions", await readiness.GetBlockerAsync(CancellationToken.None));

        session.ClosedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));

        var dispatch = new TelemetryCommandDispatch
        {
            CompanyId = company.Id,
            MachineId = machine.Id,
            OwnerNodeId = "test-node",
            Status = "Pending"
        };
        db.TelemetryCommandDispatches.Add(dispatch);
        await db.SaveChangesAsync();
        Assert.Equal("telemetry_commands_in_flight", await readiness.GetBlockerAsync(CancellationToken.None));

        dispatch.Status = "Processing";
        await db.SaveChangesAsync();
        Assert.Equal("telemetry_commands_in_flight", await readiness.GetBlockerAsync(CancellationToken.None));

        dispatch.Status = "Completed";
        await db.SaveChangesAsync();
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));
    }
}
