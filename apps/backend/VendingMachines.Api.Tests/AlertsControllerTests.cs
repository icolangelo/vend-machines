using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class AlertsControllerTests
{
    [Fact]
    public async Task AlertsAreCompanyScopedAndReadReceiptsBelongToTheCurrentUser()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var companyA = new Company { Id = Guid.NewGuid(), Name = "Empresa A", Cnpj = "11111111000111" };
        var companyB = new Company { Id = Guid.NewGuid(), Name = "Empresa B", Cnpj = "22222222000122" };
        var user = new User { Id = Guid.NewGuid(), Name = "Operador", Email = "operator@example.com", CompanyId = companyA.Id };
        var machineA = new Machine { Id = "machine-a", Name = "Máquina A", SerialNumber = "A", CompanyId = companyA.Id };
        var machineB = new Machine { Id = "machine-b", Name = "Máquina confidencial B", ClientName = "Cliente confidencial", SerialNumber = "B", CompanyId = companyB.Id };
        var alertA = new OperationalAlert { CompanyId = companyA.Id, MachineId = machineA.Id, DeduplicationKey = "offline:a", Type = "TelemetryOffline", Title = "Offline", Description = "A", Severity = "critical" };
        var alertWithForeignMachine = new OperationalAlert { CompanyId = companyA.Id, MachineId = machineB.Id, DeduplicationKey = "offline:bad-link", Type = "TelemetryOffline", Title = "Offline", Description = "A", Severity = "critical" };
        var alertB = new OperationalAlert { CompanyId = companyB.Id, DeduplicationKey = "offline:b", Type = "TelemetryOffline", Title = "Offline", Description = "B", Severity = "critical" };
        db.Companies.AddRange(companyA, companyB);
        db.Users.Add(user);
        db.Machines.AddRange(machineA, machineB);
        db.OperationalAlerts.AddRange(alertA, alertWithForeignMachine, alertB);
        await db.SaveChangesAsync();

        var controller = CreateController(db, companyA.Id, user.Id);
        var result = Assert.IsType<OkObjectResult>(await controller.GetAlerts("Active", 1, 50, CancellationToken.None));
        var payload = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal(2, payload.GetProperty("total").GetInt32());
        var foreignMachineAlert = payload.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("Id").GetGuid() == alertWithForeignMachine.Id);
        Assert.Equal(JsonValueKind.Null, foreignMachineAlert.GetProperty("MachineName").ValueKind);
        Assert.Equal(JsonValueKind.Null, foreignMachineAlert.GetProperty("ClientName").ValueKind);
        Assert.DoesNotContain("confidencial", foreignMachineAlert.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var crossCompany = Assert.IsType<NotFoundObjectResult>(await controller.MarkRead(alertB.Id, CancellationToken.None));
        Assert.Equal(0, await db.AlertReadReceipts.CountAsync());
        var ownCompany = Assert.IsType<NoContentResult>(await controller.MarkRead(alertA.Id, CancellationToken.None));
        Assert.NotNull(ownCompany);
        Assert.Equal(user.Id, (await db.AlertReadReceipts.SingleAsync()).UserId);
    }

    private static AlertsController CreateController(AppDbContext db, Guid companyId, Guid userId)
    {
        var controller = new AlertsController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim("company_id", companyId.ToString())
                    ], "test"))
                }
            }
        };
        return controller;
    }
}
