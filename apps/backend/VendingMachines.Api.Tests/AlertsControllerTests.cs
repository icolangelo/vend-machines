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

    [Fact]
    public async Task ResolvedClosureEvidenceIsVisibleOnlyInsideItsCompanyAndDoesNotExposeAuditorIdentity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var companyA = new Company { Id = Guid.NewGuid(), Name = "Empresa A", Cnpj = "11111111000111" };
        var companyB = new Company { Id = Guid.NewGuid(), Name = "Empresa B", Cnpj = "22222222000122" };
        var user = new User { Id = Guid.NewGuid(), Name = "Admin A", Email = "admin-a@example.com", CompanyId = companyA.Id };
        var alertA = new OperationalAlert
        {
            CompanyId = companyA.Id,
            SessionId = Guid.NewGuid(),
            DeduplicationKey = "session:closure-a",
            Type = "SessionFailure",
            Severity = "critical",
            Status = "Resolved",
            Title = "Sessão precisa de análise",
            Description = "Encerramento físico confirmado.",
            Resolution = "Encerramento físico confirmado por responsável autorizado. Referência de evidência: VST-2026-001."
        };
        var alertB = new OperationalAlert
        {
            CompanyId = companyB.Id,
            DeduplicationKey = "session:closure-b",
            Type = "SessionFailure",
            Severity = "critical",
            Status = "Resolved",
            Title = "Sessão precisa de análise",
            Description = "Encerramento físico confirmado.",
            Resolution = "Referência de evidência: PRIVADO-B-2026."
        };
        db.Companies.AddRange(companyA, companyB);
        db.Users.Add(user);
        db.OperationalAlerts.AddRange(alertA, alertB);
        await db.SaveChangesAsync();

        var controller = CreateController(db, companyA.Id, user.Id);
        var result = Assert.IsType<OkObjectResult>(await controller.GetAlerts("Resolved", 1, 50, CancellationToken.None));
        var payload = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal(1, payload.GetProperty("total").GetInt32());
        var resolvedAlert = Assert.Single(payload.GetProperty("items").EnumerateArray());
        Assert.Equal(alertA.Id, resolvedAlert.GetProperty("Id").GetGuid());
        Assert.Equal(alertA.Resolution, resolvedAlert.GetProperty("Resolution").GetString());
        Assert.DoesNotContain("PRIVADO-B-2026", payload.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(user.Id.ToString(), payload.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SummaryCountsActiveAlertsAcrossPagesAndStaysWithinCompanyMachineScope()
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
        var machineB = new Machine { Id = "machine-b", Name = "Máquina B", SerialNumber = "B", CompanyId = companyB.Id };
        db.Companies.AddRange(companyA, companyB);
        db.Users.Add(user);
        db.Machines.AddRange(machineA, machineB);
        db.OperationalAlerts.AddRange(
            new OperationalAlert { CompanyId = companyA.Id, MachineId = machineA.Id, DeduplicationKey = "offline:a:1", Type = "TelemetryOffline", Title = "Offline 1", Description = "A", Severity = "critical" },
            new OperationalAlert { CompanyId = companyA.Id, MachineId = machineA.Id, DeduplicationKey = "offline:a:2", Type = "TelemetryOffline", Title = "Offline 2", Description = "A", Severity = "critical" },
            new OperationalAlert { CompanyId = companyA.Id, MachineId = machineB.Id, DeduplicationKey = "offline:foreign", Type = "TelemetryOffline", Title = "Offline foreign", Description = "B", Severity = "critical" },
            new OperationalAlert { CompanyId = companyA.Id, DeduplicationKey = "warning:a:1", Type = "LowStock", Title = "Low stock 1", Description = "A", Severity = "warning" },
            new OperationalAlert { CompanyId = companyA.Id, DeduplicationKey = "warning:a:2", Type = "PaymentPending", Title = "Payment pending", Description = "A", Severity = "warning" },
            new OperationalAlert { CompanyId = companyA.Id, MachineId = machineA.Id, DeduplicationKey = "offline:resolved", Type = "TelemetryOffline", Title = "Resolved", Description = "A", Severity = "critical", Status = "Resolved" },
            new OperationalAlert { CompanyId = companyB.Id, MachineId = machineB.Id, DeduplicationKey = "warning:b", Type = "LowStock", Title = "Low stock B", Description = "B", Severity = "warning" });
        await db.SaveChangesAsync();

        var controller = CreateController(db, companyA.Id, user.Id);
        var result = Assert.IsType<OkObjectResult>(await controller.GetSummary(CancellationToken.None));
        var payload = JsonSerializer.SerializeToElement(result.Value);

        Assert.Equal(1, payload.GetProperty("DisconnectedMachines").GetInt32());
        Assert.Equal(2, payload.GetProperty("OtherActiveAlerts").GetInt32());
    }

    [Fact]
    public async Task AlertsCanBeFilteredAndPagedWhileReturningTheFullMatchCount()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa", Cnpj = "11111111000111" };
        var user = new User { Id = Guid.NewGuid(), Name = "Operador", Email = "operator@example.com", CompanyId = company.Id };
        var machine = new Machine { Id = "machine-b", Name = "Máquina B", ClientName = "Unidade B", SerialNumber = "SERIAL-B", CompanyId = company.Id };
        db.Companies.Add(company);
        db.Users.Add(user);
        db.Machines.Add(machine);
        db.OperationalAlerts.AddRange(
            new OperationalAlert { CompanyId = company.Id, MachineId = machine.Id, DeduplicationKey = "stock:1", Type = "LowStock", Title = "Estoque baixo 1", Description = "Reposição", Severity = "warning", UpdatedAt = DateTime.UtcNow },
            new OperationalAlert { CompanyId = company.Id, MachineId = machine.Id, DeduplicationKey = "stock:2", Type = "LowStock", Title = "Estoque baixo 2", Description = "Reposição", Severity = "warning", UpdatedAt = DateTime.UtcNow.AddMinutes(-1) },
            new OperationalAlert { CompanyId = company.Id, MachineId = machine.Id, DeduplicationKey = "offline:1", Type = "TelemetryOffline", Title = "Desconectada", Description = "Sem comunicação", Severity = "critical" });
        await db.SaveChangesAsync();

        var controller = CreateController(db, company.Id, user.Id);
        var result = Assert.IsType<OkObjectResult>(await controller.GetAlerts(
            "Active", 2, 1, CancellationToken.None, "OtherActive", "Máquina B"));
        var payload = JsonSerializer.SerializeToElement(result.Value);

        Assert.Equal(2, payload.GetProperty("total").GetInt32());
        Assert.Equal(2, payload.GetProperty("page").GetInt32());
        Assert.Single(payload.GetProperty("items").EnumerateArray());
        Assert.Equal("Estoque baixo 2", payload.GetProperty("items")[0].GetProperty("Title").GetString());

        var machineIdSearch = Assert.IsType<OkObjectResult>(await controller.GetAlerts(
            "Active", 1, 50, CancellationToken.None, "OtherActive", "machine-b"));
        var machineIdPayload = JsonSerializer.SerializeToElement(machineIdSearch.Value);
        Assert.Equal(2, machineIdPayload.GetProperty("total").GetInt32());

        var serialSearch = Assert.IsType<OkObjectResult>(await controller.GetAlerts(
            "Active", 1, 50, CancellationToken.None, "OtherActive", "SERIAL-B"));
        var serialPayload = JsonSerializer.SerializeToElement(serialSearch.Value);
        Assert.Equal(2, serialPayload.GetProperty("total").GetInt32());
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
