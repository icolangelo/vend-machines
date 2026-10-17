using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class MachinesControllerTelemetryIsolationTests
{
    [Fact]
    public async Task EnablingAutomaticSessionIsRejectedWhenTelemetryStateBelongsToAnotherCompany()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa A", Cnpj = "11111111000111" };
        var foreignCompany = new Company { Id = Guid.NewGuid(), Name = "Empresa B", Cnpj = "22222222000122" };
        var location = new Location { CompanyId = company.Id, Name = "Unidade" };
        var machine = new Machine
        {
            Id = "machine-a", Name = "Máquina", SerialNumber = "SERIAL-A", CompanyId = company.Id,
            LocationId = location.Id, AutoOpenSessionEnabled = false
        };
        var foreignState = new MachineConnectionState
        {
            MachineId = machine.Id, CompanyId = foreignCompany.Id, MonitoringEnabled = true,
            AutoOpenLastError = "erro da outra empresa"
        };
        db.Companies.AddRange(company, foreignCompany);
        db.Locations.Add(location);
        db.Machines.Add(machine);
        db.MachineConnectionStates.Add(foreignState);
        await db.SaveChangesAsync();

        var result = Update(db, company.Id, machine, autoOpen: true);

        Assert.IsType<BadRequestObjectResult>(result);
        db.ChangeTracker.Clear();
        Assert.False((await db.Machines.SingleAsync(x => x.Id == machine.Id)).AutoOpenSessionEnabled);
        Assert.Equal(foreignCompany.Id, (await db.MachineConnectionStates.SingleAsync()).CompanyId);
        Assert.True((await db.MachineConnectionStates.SingleAsync()).MonitoringEnabled);
        Assert.Equal("erro da outra empresa", (await db.MachineConnectionStates.SingleAsync()).AutoOpenLastError);
        Assert.Empty(await db.TelemetryEvents.ToListAsync());
    }

    [Fact]
    public async Task DisablingAutomaticSessionDoesNotMutateForeignTelemetryState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa A", Cnpj = "11111111000111" };
        var foreignCompany = new Company { Id = Guid.NewGuid(), Name = "Empresa B", Cnpj = "22222222000122" };
        var location = new Location { CompanyId = company.Id, Name = "Unidade" };
        var machine = new Machine
        {
            Id = "machine-a", Name = "Máquina", SerialNumber = "SERIAL-A", CompanyId = company.Id,
            LocationId = location.Id, AutoOpenSessionEnabled = true
        };
        var foreignState = new MachineConnectionState
        {
            MachineId = machine.Id, CompanyId = foreignCompany.Id, MonitoringEnabled = true,
            AutoOpenLastError = "erro da outra empresa"
        };
        db.Companies.AddRange(company, foreignCompany);
        db.Locations.Add(location);
        db.Machines.Add(machine);
        db.MachineConnectionStates.Add(foreignState);
        await db.SaveChangesAsync();

        var result = Update(db, company.Id, machine, autoOpen: false);

        Assert.IsType<NoContentResult>(result);
        db.ChangeTracker.Clear();
        Assert.False((await db.Machines.SingleAsync(x => x.Id == machine.Id)).AutoOpenSessionEnabled);
        var persistedState = await db.MachineConnectionStates.SingleAsync();
        Assert.Equal(foreignCompany.Id, persistedState.CompanyId);
        Assert.True(persistedState.MonitoringEnabled);
        Assert.Equal("erro da outra empresa", persistedState.AutoOpenLastError);
        Assert.Single(await db.TelemetryEvents.ToListAsync());
    }

    private static ActionResult Update(AppDbContext db, Guid companyId, Machine existing, bool autoOpen)
    {
        var controller = new MachinesController(new DatabaseDataService(db), db, connections: null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("company_id", companyId.ToString())], "test"))
                }
            }
        };

        return controller.Update(existing.Id, new Machine
        {
            Id = existing.Id,
            Name = existing.Name,
            SerialNumber = existing.SerialNumber,
            CompanyId = companyId,
            LocationId = existing.LocationId,
            AutoOpenSessionEnabled = autoOpen
        });
    }
}
