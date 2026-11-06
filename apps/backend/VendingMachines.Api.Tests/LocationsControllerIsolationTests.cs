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

public sealed class LocationsControllerIsolationTests
{
    [Fact]
    public async Task GetByIdDoesNotAggregateMachineFromAnotherCompanyWithMismatchedLocationId()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var companyA = new Company { Id = Guid.NewGuid(), Name = "Empresa A" };
        var companyB = new Company { Id = Guid.NewGuid(), Name = "Empresa B" };
        var locationA = new Location { Id = Guid.NewGuid(), CompanyId = companyA.Id, Name = "Local A" };
        db.Companies.AddRange(companyA, companyB);
        db.Locations.Add(locationA);
        db.Machines.AddRange(
            new Machine
            {
                Id = "machine-a",
                Name = "Máquina A",
                SerialNumber = "SERIAL-A",
                CompanyId = companyA.Id,
                LocationId = locationA.Id,
                Revenue30d = 12m,
                TotalSales30d = 3
            },
            new Machine
            {
                Id = "machine-b",
                Name = "Máquina B",
                SerialNumber = "SERIAL-B",
                CompanyId = companyB.Id,
                LocationId = locationA.Id,
                Revenue30d = 900m,
                TotalSales30d = 90
            });
        var sessionAId = Guid.NewGuid();
        var transactionAId = Guid.NewGuid();
        var sessionBId = Guid.NewGuid();
        var transactionBId = Guid.NewGuid();
        db.MachineSessions.AddRange(
            new MachineSession { Id = sessionAId, CompanyId = companyA.Id, MachineId = "machine-a" },
            new MachineSession { Id = sessionBId, CompanyId = companyB.Id, MachineId = "machine-b" });
        db.PaymentTransactions.AddRange(
            new PaymentTransaction { Id = transactionAId, CompanyId = companyA.Id, MachineId = "machine-a", Amount = 12m },
            new PaymentTransaction { Id = transactionBId, CompanyId = companyB.Id, MachineId = "machine-b", Amount = 900m });
        db.SaleRecords.AddRange(
            new SaleRecord
            {
                CompanyId = companyA.Id,
                MachineId = "machine-a",
                SessionId = sessionAId,
                TransactionId = transactionAId,
                ItemNumber = 1,
                Quantity = 3,
                GrossAmount = 12m,
                SoldAt = DateTime.UtcNow
            },
            new SaleRecord
            {
                CompanyId = companyB.Id,
                MachineId = "machine-b",
                SessionId = sessionBId,
                TransactionId = transactionBId,
                ItemNumber = 1,
                Quantity = 90,
                GrossAmount = 900m,
                SoldAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var controller = new LocationsController(db, new DatabaseDataService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.Role, AppRoles.Operator),
                    new Claim("company_id", companyA.Id.ToString())
                }, "Test"))
            }
        };

        var action = await controller.GetById(locationA.Id);

        var result = Assert.IsType<OkObjectResult>(action);
        var location = Assert.IsType<LocationDto>(result.Value);
        Assert.Equal(1, location.MachineCount);
        Assert.Equal(12m, location.Revenue30d);
        Assert.Equal(3, location.TotalSales30d);
    }
}
