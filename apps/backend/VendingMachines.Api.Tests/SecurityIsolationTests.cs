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

public sealed class SecurityIsolationTests
{
    [Fact]
    public async Task UsersController_CompanyAdminCannotListUsersFromAnotherCompany()
    {
        await using var scenario = await CreateScenarioAsync();
        var companyA = new Company { Id = Guid.NewGuid(), Name = "Empresa A" };
        var companyB = new Company { Id = Guid.NewGuid(), Name = "Empresa B" };
        scenario.Db.Companies.AddRange(companyA, companyB);
        scenario.Db.Users.AddRange(
            new User { Id = Guid.NewGuid(), Name = "Usuário A", Email = "a@example.com", CompanyId = companyA.Id },
            new User { Id = Guid.NewGuid(), Name = "Usuário B", Email = "b@example.com", CompanyId = companyB.Id });
        await scenario.Db.SaveChangesAsync();

        var controller = new UsersController(scenario.Db);
        SetUser(controller, companyA.Id, "admin-a@example.com");

        var action = await controller.GetPaginatedUsers();
        var result = Assert.IsType<OkObjectResult>(action);
        var page = Assert.IsType<PaginatedResult<UserDto>>(result.Value);

        var user = Assert.Single(page.Items);
        Assert.Equal("Usuário A", user.Name);
    }

    [Fact]
    public async Task PaymentsController_CompanyAdminCannotReadAnotherCompanyTransactionLogs()
    {
        await using var scenario = await CreateScenarioAsync();
        var companyA = new Company { Id = Guid.NewGuid(), Name = "Empresa A" };
        var companyB = new Company { Id = Guid.NewGuid(), Name = "Empresa B" };
        var machineA = new Machine { Id = "machine-a", Name = "A", SerialNumber = "A", CompanyId = companyA.Id };
        var machineB = new Machine { Id = "machine-b", Name = "B", SerialNumber = "B", CompanyId = companyB.Id };
        var transactionB = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            MachineId = machineB.Id,
            CompanyId = companyB.Id,
            Amount = 10m,
            Status = "Approved"
        };
        var transactionA = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            MachineId = machineA.Id,
            CompanyId = companyA.Id,
            Amount = 10m,
            Status = "Failed",
            RawResponse = "payer_email=cliente-secreto@example.com"
        };
        scenario.Db.Companies.AddRange(companyA, companyB);
        scenario.Db.Machines.AddRange(machineA, machineB);
        scenario.Db.PaymentTransactions.AddRange(transactionA, transactionB);
        scenario.Db.TransactionTelemetryLogs.Add(new TransactionTelemetryLog
        {
            TransactionId = transactionB.Id,
            Message = "segredo da empresa B"
        });
        await scenario.Db.SaveChangesAsync();

        var controller = new PaymentsController(
            scenario.Db,
            sessionOrchestrator: null!,
            scopeFactory: null!,
            configuration: null!,
            environment: null!);
        SetUser(controller, companyA.Id, "admin-a@example.com");

        var action = await controller.GetTransactionLogs(transactionB.Id);

        var notFound = Assert.IsType<NotFoundObjectResult>(action);
        Assert.Contains("não encontrada", notFound.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

        var transactionList = Assert.IsType<OkObjectResult>(await controller.GetTransactions());
        var transactionJson = JsonSerializer.Serialize(transactionList.Value);
        Assert.DoesNotContain("RawResponse", transactionJson);
        Assert.DoesNotContain("cliente-secreto@example.com", transactionJson);
    }

    [Fact]
    public async Task PaymentsController_ReconciliationQueueIsPlatformAdminOnlyAndOmitsRawProviderResponse()
    {
        await using var scenario = await CreateScenarioAsync();
        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa A" };
        var machine = new Machine { Id = "machine-a", Name = "Máquina A", SerialNumber = "A", CompanyId = company.Id };
        scenario.Db.Companies.Add(company);
        scenario.Db.Machines.Add(machine);
        scenario.Db.PaymentTransactions.AddRange(
            new PaymentTransaction
            {
                Id = Guid.NewGuid(), CompanyId = company.Id, MachineId = machine.Id, Amount = 12m,
                Status = "Pending", MercadoPagoStatus = "pending", RawResponse = "resposta sensível"
            },
            new PaymentTransaction
            {
                Id = Guid.NewGuid(), CompanyId = company.Id, MachineId = machine.Id, Amount = 20m,
                Status = "Approved", RawResponse = "não deve aparecer"
            });
        await scenario.Db.SaveChangesAsync();

        var companyAdminController = CreatePaymentsController(scenario.Db);
        SetUser(companyAdminController, company.Id, "admin-a@example.com");
        var forbidden = Assert.IsType<ObjectResult>(await companyAdminController.GetReconciliationQueue());
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        var platformAdminController = CreatePaymentsController(scenario.Db);
        SetUser(platformAdminController, Guid.Empty, "platform-admin@example.com", "PlatformAdmin");
        var result = Assert.IsType<OkObjectResult>(await platformAdminController.GetReconciliationQueue(pageSize: 10));
        var response = JsonSerializer.Serialize(result.Value);
        Assert.Contains("Empresa A", response);
        Assert.Contains("machine-a", response);
        Assert.Contains("Pending", response);
        Assert.DoesNotContain("Approved", response);
        Assert.DoesNotContain("RawResponse", response);
        Assert.DoesNotContain("resposta sensível", response);
    }

    [Fact]
    public async Task PaymentsController_AdminLogsOmitsLegacyProviderBodiesAndBoundsPagination()
    {
        await using var scenario = await CreateScenarioAsync();
        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa A" };
        var machine = new Machine { Id = "machine-a", Name = "Máquina A", SerialNumber = "A", CompanyId = company.Id };
        var transaction = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            MachineId = machine.Id,
            Amount = 12m,
            Status = "Failed",
            RawResponse = "payer_email=cliente-secreto@example.com"
        };
        scenario.Db.Companies.Add(company);
        scenario.Db.Machines.Add(machine);
        scenario.Db.PaymentTransactions.Add(transaction);
        scenario.Db.TransactionTelemetryLogs.Add(new TransactionTelemetryLog
        {
            TransactionId = transaction.Id,
            LogType = "Error",
            Message = "Erro retornado pelo Mercado Pago: payer_email=cliente-secreto@example.com"
        });
        await scenario.Db.SaveChangesAsync();

        var companyAdmin = CreatePaymentsController(scenario.Db);
        SetUser(companyAdmin, company.Id, "admin-a@example.com");
        var forbidden = Assert.IsType<ObjectResult>(await companyAdmin.GetAdminLogs());
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        var platformAdmin = CreatePaymentsController(scenario.Db);
        SetUser(platformAdmin, Guid.Empty, "platform-admin@example.com", "PlatformAdmin");
        var result = Assert.IsType<OkObjectResult>(await platformAdmin.GetAdminLogs(page: 0, pageSize: 500));
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var root = response.RootElement;

        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(200, root.GetProperty("pageSize").GetInt32());
        Assert.Single(root.GetProperty("items").EnumerateArray());
        var item = root.GetProperty("items")[0];
        Assert.Equal(
            "Falha na integração de pagamento. Detalhes sensíveis foram omitidos.",
            item.GetProperty("Message").GetString());
        var json = root.GetRawText();
        Assert.DoesNotContain("RawResponse", json);
        Assert.DoesNotContain("cliente-secreto@example.com", json);
        Assert.DoesNotContain("Erro retornado pelo Mercado Pago", json);
    }

    [Fact]
    public async Task CompaniesController_OmitsLegacyProviderBodyFromIntegrationDiagnostics()
    {
        await using var scenario = await CreateScenarioAsync();
        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa A" };
        scenario.Db.Companies.Add(company);
        scenario.Db.MercadoPagoIntegrations.Add(new MercadoPagoIntegration
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            LastTokenValidationStatus = "Falha ao renovar token OAuth: access_token=segredo-cliente"
        });
        await scenario.Db.SaveChangesAsync();

        var controller = new CompaniesController(scenario.Db);
        SetUser(controller, company.Id, "platform-admin@example.com", "PlatformAdmin");
        var result = Assert.IsType<OkObjectResult>(await controller.GetCompanyIntegration(company.Id));
        var response = JsonSerializer.Serialize(result.Value);

        Assert.DoesNotContain("access_token=segredo-cliente", response);
        Assert.Contains("omitidos", response);
    }

    private static PaymentsController CreatePaymentsController(AppDbContext db) => new(
        db,
        sessionOrchestrator: null!,
        scopeFactory: null!,
        configuration: null!,
        environment: null!);

    [Fact]
    public async Task AuthController_RejectsRegistrationWithReservedPlatformAdminEmail()
    {
        await using var scenario = await CreateScenarioAsync();
        var controller = new AuthController(
            scenario.Db,
            tokenService: null!,
            refreshSessions: null!,
            cookieService: null!);

        var action = await controller.Register(new RegisterRequest
        {
            Name = "Tentativa",
            Email = " DEV.IVAN@GMAIL.COM ",
            Password = "senha-segura", // gitleaks:allow
            Cpf = "529.982.247-25",
            CompanyName = "Empresa indevida",
            CompanyCnpj = "12.345.678/0001-95",
            AcceptedPrivacyPolicy = true
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Contains("reservado", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthController_DoesNotReturnDatabaseErrorsDuringRegistration()
    {
        await using var scenario = await CreateScenarioAsync();
        await using (var command = scenario.Db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER FailCompanyRegistration
                BEFORE INSERT ON Companies
                BEGIN
                    SELECT RAISE(ABORT, 'internal database detail');
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var controller = new AuthController(
            scenario.Db,
            tokenService: null!,
            refreshSessions: null!,
            cookieService: null!);

        var action = await controller.Register(new RegisterRequest
        {
            Name = "Usuário de teste",
            Email = "novo@example.com",
            Password = "senha-com-12-caracteres", // gitleaks:allow
            Cpf = "529.982.247-25",
            CompanyName = "Empresa de teste",
            CompanyCnpj = "12.345.678/0001-95",
            AcceptedPrivacyPolicy = true
        });

        var result = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        var message = result.Value?.GetType().GetProperty("message")?.GetValue(result.Value)?.ToString();
        Assert.Contains("Não foi possível concluir o cadastro", message);
        Assert.DoesNotContain("internal database detail", message);
    }

    private static void SetUser(ControllerBase controller, Guid companyId, string email, string role = "Admin")
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.Email, email),
                    new Claim("company_id", companyId.ToString()),
                    new Claim(ClaimTypes.Role, role)
                }, "Test"))
            }
        };
    }

    private static async Task<TestScenario> CreateScenarioAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
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
