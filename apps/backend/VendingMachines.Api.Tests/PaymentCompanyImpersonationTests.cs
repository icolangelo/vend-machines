using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Middlewares;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class PaymentCompanyImpersonationTests
{
    [Fact]
    public async Task TransactionHistoryUsesImpersonatedCompanyInsteadOfPlatformAdminsOwnCompany()
    {
        await using var scenario = await CreateScenarioAsync();
        var (platformCompany, selectedCompany) = AddCompanies(scenario.Db);
        AddMachineAndTransaction(scenario.Db, platformCompany, "platform-machine", "platform-payment");
        AddMachineAndTransaction(scenario.Db, selectedCompany, "selected-machine", "selected-payment");
        await scenario.Db.SaveChangesAsync();

        var controller = CreateController(scenario.Db);
        SetPlatformAdminImpersonating(controller, platformCompany.Id, selectedCompany.Id);

        var result = Assert.IsType<OkObjectResult>(await controller.GetTransactions());
        var json = JsonSerializer.Serialize(result.Value);

        Assert.Contains("selected-payment", json);
        Assert.DoesNotContain("platform-payment", json);
    }

    [Fact]
    public async Task ToggleIntegrationChangesOnlyTheImpersonatedCompany()
    {
        await using var scenario = await CreateScenarioAsync();
        var (platformCompany, selectedCompany) = AddCompanies(scenario.Db);
        scenario.Db.MercadoPagoIntegrations.AddRange(
            new MercadoPagoIntegration { CompanyId = platformCompany.Id, IsActive = false },
            new MercadoPagoIntegration { CompanyId = selectedCompany.Id, IsActive = false });
        await scenario.Db.SaveChangesAsync();

        var controller = CreateController(scenario.Db);
        SetPlatformAdminImpersonating(controller, platformCompany.Id, selectedCompany.Id);

        Assert.IsType<OkObjectResult>(await controller.ToggleIntegration(
            new PaymentsController.ToggleIntegrationRequest { IsActive = true }));

        Assert.False(await scenario.Db.MercadoPagoIntegrations
            .Where(integration => integration.CompanyId == platformCompany.Id)
            .Select(integration => integration.IsActive)
            .SingleAsync());
        Assert.True(await scenario.Db.MercadoPagoIntegrations
            .Where(integration => integration.CompanyId == selectedCompany.Id)
            .Select(integration => integration.IsActive)
            .SingleAsync());
    }

    [Theory]
    [InlineData(0, 200, 503)]
    [InlineData(100, 200, 503)]
    [InlineData(5, 1, 400)]
    [InlineData(99.9, 1, 400)]
    public async Task DirectPixRejectsInvalidEffectivePlatformFeeBeforeCreatingTransaction(
        decimal feePercent,
        int amountCents,
        int expectedStatusCode)
    {
        await using var scenario = await CreateScenarioAsync();
        var company = new Company
        {
            Id = Guid.NewGuid(),
            Name = "Empresa de teste",
            Cnpj = "00000000000300",
            NormalizedCnpj = "00000000000300"
        };
        var machine = new Machine
        {
            Id = "fee-test-machine",
            Name = "Máquina de teste",
            SerialNumber = "SERIAL-FEE-TEST",
            CompanyId = company.Id,
            MercadoPagoEnabled = true
        };
        scenario.Db.AddRange(
            company,
            machine,
            new MercadoPagoIntegration { CompanyId = company.Id, IsActive = true },
            new SystemSettings { ApplicationFeePercent = feePercent });
        await scenario.Db.SaveChangesAsync();

        var controller = CreateController(scenario.Db);
        SetPlatformAdminImpersonating(controller, company.Id, company.Id);
        var result = await controller.CreatePixQrCode(new PixChargeRequest
        {
            MachineId = machine.Id,
            Amount = amountCents / 100m,
            UseRealMercadoPago = true
        });

        if (expectedStatusCode == StatusCodes.Status503ServiceUnavailable)
            Assert.Equal(expectedStatusCode, Assert.IsType<ObjectResult>(result).StatusCode);
        else
            Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await scenario.Db.PaymentTransactions.AsNoTracking().ToListAsync());
    }

    private static PaymentsController CreateController(AppDbContext db) => new(
        db,
        sessionOrchestrator: null!,
        scopeFactory: null!,
        configuration: new ConfigurationBuilder().Build(),
        environment: null!);

    private static void SetPlatformAdminImpersonating(
        ControllerBase controller,
        Guid platformCompanyId,
        Guid selectedCompanyId)
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("company_id", platformCompanyId.ToString()),
                    new Claim(ClaimTypes.Role, AppRoles.PlatformAdmin),
                    new Claim(ImpersonationMiddleware.ImpersonatedCompanyIdClaim, selectedCompanyId.ToString())
                ], "Test"))
            }
        };
    }

    private static (Company PlatformCompany, Company SelectedCompany) AddCompanies(AppDbContext db)
    {
        var platformCompany = new Company
        {
            Id = Guid.NewGuid(),
            Name = "Empresa do administrador",
            Cnpj = "00000000000100",
            NormalizedCnpj = "00000000000100"
        };
        var selectedCompany = new Company
        {
            Id = Guid.NewGuid(),
            Name = "Empresa selecionada",
            Cnpj = "00000000000200",
            NormalizedCnpj = "00000000000200"
        };
        db.Companies.AddRange(platformCompany, selectedCompany);
        return (platformCompany, selectedCompany);
    }

    private static void AddMachineAndTransaction(AppDbContext db, Company company, string machineId, string paymentId)
    {
        var machine = new Machine
        {
            Id = machineId,
            Name = machineId,
            SerialNumber = $"SERIAL-{machineId}",
            CompanyId = company.Id
        };
        db.Machines.Add(machine);
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            CompanyId = company.Id,
            MachineId = machine.Id,
            Machine = machine,
            Amount = 10m,
            Status = "Pending",
            MercadoPagoPaymentId = paymentId
        });
    }

    private static async Task<TestScenario> CreateScenarioAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return new TestScenario(connection, db);
    }

    private sealed class TestScenario(SqliteConnection connection, AppDbContext db) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
