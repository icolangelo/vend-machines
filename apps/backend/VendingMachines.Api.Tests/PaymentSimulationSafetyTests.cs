using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class PaymentSimulationSafetyTests
{
    [Fact]
    public async Task SimulatedPixIsRejectedOutsideDevelopmentBeforeCreatingTransaction()
    {
        await using var scenario = await CreateScenarioAsync();
        var controller = CreateController(scenario.Db, Environments.Production, new UnexpectedHttpClientFactory());

        var result = Assert.IsType<BadRequestObjectResult>(await controller.CreatePixQrCode(new PixChargeRequest
        {
            UseRealMercadoPago = false
        }));

        Assert.Contains("desenvolvimento", JsonSerializer.Serialize(result.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await scenario.Db.PaymentTransactions.ToListAsync());
    }

    [Fact]
    public async Task SimulatedWebhookIsNotFoundOutsideDevelopmentWithoutChangingTransactions()
    {
        await using var scenario = await CreateScenarioAsync();
        var controller = CreateController(scenario.Db, Environments.Production, new UnexpectedHttpClientFactory());
        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa de teste" };
        var machine = new Machine
        {
            Id = "machine-test",
            Name = "Máquina de teste",
            SerialNumber = "SERIAL-TEST",
            CompanyId = company.Id
        };
        var transaction = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            MachineId = machine.Id,
            CompanyId = company.Id,
            Amount = 10m,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };
        scenario.Db.Companies.Add(company);
        scenario.Db.Machines.Add(machine);
        scenario.Db.PaymentTransactions.Add(transaction);
        await scenario.Db.SaveChangesAsync();
        SetUser(controller, company.Id, "admin@example.com");

        var result = await controller.SimulateWebhook(new SimulateWebhookRequest
        {
            TransactionId = transaction.Id,
            Approved = true,
            SendTelemetryToMachine = true
        });

        Assert.IsType<NotFoundResult>(result);
        var persistedTransaction = await scenario.Db.PaymentTransactions.SingleAsync();
        Assert.Equal("Pending", persistedTransaction.Status);
        Assert.Null(persistedTransaction.CompletedAt);
        Assert.Empty(await scenario.Db.TransactionTelemetryLogs.ToListAsync());
    }

    [Fact]
    public async Task DevelopmentSimulationDoesNotContactProviderEvenWithRealLookingCredentials()
    {
        await using var scenario = await CreateScenarioAsync();
        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa de teste" };
        var machine = new Machine
        {
            Id = "machine-test",
            Name = "Máquina de teste",
            SerialNumber = "SERIAL-TEST",
            CompanyId = company.Id,
            MercadoPagoEnabled = true
        };
        scenario.Db.Companies.Add(company);
        scenario.Db.Machines.Add(machine);
        scenario.Db.MercadoPagoIntegrations.Add(new MercadoPagoIntegration
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            IsActive = true,
            AccessToken = "APP_USR_realistic_test_access_token_123456",
            RefreshToken = string.Empty,
            ClientSecret = string.Empty
        });
        await scenario.Db.SaveChangesAsync();

        var httpClientFactory = new UnexpectedHttpClientFactory();
        var controller = CreateController(scenario.Db, Environments.Development, httpClientFactory);
        SetUser(controller, company.Id, "admin@example.com");
        var result = Assert.IsType<OkObjectResult>(await controller.CreatePixQrCode(new PixChargeRequest
        {
            MachineId = machine.Id,
            Amount = 10m,
            UseRealMercadoPago = false
        }));

        var payload = JsonSerializer.SerializeToElement(result.Value);
        Assert.True(payload.GetProperty("simulated").GetBoolean());
        Assert.Equal(0, httpClientFactory.CreateCount);
        var transaction = await scenario.Db.PaymentTransactions.SingleAsync();
        Assert.StartsWith("mock_mp_", transaction.MercadoPagoPaymentId);
        Assert.Equal("Pending", transaction.Status);
    }

    private static PaymentsController CreateController(
        AppDbContext db,
        string environmentName,
        IHttpClientFactory httpClientFactory) => new(
            db,
            sessionOrchestrator: null!,
            scopeFactory: null!,
            configuration: new ConfigurationBuilder().Build(),
            environment: new TestWebHostEnvironment(environmentName),
            httpClientFactory: httpClientFactory);

    private static void SetUser(ControllerBase controller, Guid companyId, string email)
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Email, email),
                    new Claim("company_id", companyId.ToString()),
                    new Claim(ClaimTypes.Role, "Admin")
                ], "Test"))
            }
        };
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

    private sealed class UnexpectedHttpClientFactory : IHttpClientFactory
    {
        public int CreateCount { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateCount++;
            throw new InvalidOperationException("A simulação não pode chamar o provedor de pagamentos.");
        }
    }

    private sealed class TestWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = nameof(PaymentSimulationSafetyTests);
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string EnvironmentName { get; set; } = environmentName;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
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
