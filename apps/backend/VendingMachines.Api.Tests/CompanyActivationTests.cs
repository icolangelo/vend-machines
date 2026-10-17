using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Middlewares;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class CompanyActivationTests
{
    [Fact]
    public async Task PublicRegistrationCreatesPendingCompanyWithoutSessionOrSampleLocations()
    {
        await using var scenario = await CreateScenarioAsync();
        var controller = new AuthController(scenario.Db, null!, null!, null!);

        var result = await controller.Register(new RegisterRequest
        {
            Name = "Pessoa responsável",
            Email = "responsavel@example.com",
            Password = "senha-segura-com-12", // gitleaks:allow
            Cpf = "529.982.247-25",
            CompanyName = "Empresa Nova",
            CompanyCnpj = "12.345.678/0001-95",
            AcceptedPrivacyPolicy = true
        });

        var accepted = Assert.IsType<AcceptedResult>(result);
        var message = accepted.Value?.GetType().GetProperty("message")?.GetValue(accepted.Value)?.ToString();
        Assert.Contains("confirmação", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("solicitar o envio novamente", message, StringComparison.OrdinalIgnoreCase);
        var company = await scenario.Db.Companies.SingleAsync();
        Assert.Equal(CompanyActivationStatuses.Pending, company.ActivationStatus);
        Assert.Equal(0, await scenario.Db.Locations.CountAsync());
        Assert.Equal(0, await scenario.Db.RefreshSessions.CountAsync());
        Assert.False((await scenario.Db.Users.SingleAsync()).IsActive);

        var login = await controller.Login(new LoginRequest
        {
            Email = "responsavel@example.com",
            Password = "senha-segura-com-12" // gitleaks:allow
        });
        Assert.IsType<UnauthorizedObjectResult>(login);
    }

    [Fact]
    public async Task PendingCompanyCannotLoginOrUseAnExistingAccessToken()
    {
        await using var scenario = await CreateScenarioAsync();
        var company = new Company { Name = "Empresa Pendente", ActivationStatus = CompanyActivationStatuses.Pending };
        var user = new User
        {
            Name = "Responsável",
            Email = "responsavel@example.com",
            NormalizedEmail = "responsavel@example.com",
            Role = AppRoles.Admin,
            CompanyId = company.Id,
            IsActive = true
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "senha-segura-com-12");
        scenario.Db.Companies.Add(company);
        scenario.Db.Users.Add(user);
        await scenario.Db.SaveChangesAsync();

        var auth = new AuthController(scenario.Db, null!, null!, null!);
        var login = await auth.Login(new LoginRequest { Email = user.Email, Password = "senha-segura-com-12" }); // gitleaks:allow
        Assert.IsType<UnauthorizedObjectResult>(login);

        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Role, AppRoles.Admin),
            new Claim("credential_version", "0")
        }, "test"));
        var nextCalled = false;
        var middleware = new UserRoleValidationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, scenario.Db);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvitationCannotCreateAUserForPendingCompany()
    {
        await using var scenario = await CreateScenarioAsync();
        var company = new Company { Name = "Empresa Pendente", ActivationStatus = CompanyActivationStatuses.Pending };
        const string rawToken = "invitation-token-for-pending-company";
        scenario.Db.Companies.Add(company);
        scenario.Db.UserInvitations.Add(new UserInvitation
        {
            CompanyId = company.Id,
            CreatedByUserId = Guid.NewGuid(),
            Email = "operator@example.com",
            Role = AppRoles.Operator,
            TokenHash = OneTimeTokens.Hash(rawToken),
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        });
        await scenario.Db.SaveChangesAsync();

        var controller = new AuthController(scenario.Db, null!, null!, null!);
        var result = await controller.AcceptInvitation(new AcceptInvitationRequest
        {
            InvitationToken = rawToken,
            Name = "Operador",
            Cpf = "529.982.247-25",
            Password = "senha-segura-com-12", // gitleaks:allow
            AcceptedPrivacyPolicy = true
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await scenario.Db.Users.ToListAsync());
        Assert.Null((await scenario.Db.UserInvitations.SingleAsync()).AcceptedAt);
    }

    [Fact]
    public async Task RejectingCompanyDisablesUsersAndRevokesTheirRefreshSessions()
    {
        await using var scenario = await CreateScenarioAsync();
        var company = new Company { Name = "Empresa em análise" };
        var activeUser = new User { Name = "Admin", Email = "admin@example.com", CompanyId = company.Id, CredentialVersion = 3 };
        var alreadyInactiveUser = new User { Name = "Operador", Email = "operator@example.com", CompanyId = company.Id, IsActive = false, CredentialVersion = 7 };
        scenario.Db.Companies.Add(company);
        scenario.Db.Users.AddRange(activeUser, alreadyInactiveUser);
        scenario.Db.RefreshSessions.AddRange(
            NewRefreshSession(activeUser),
            NewRefreshSession(alreadyInactiveUser));
        await scenario.Db.SaveChangesAsync();

        var audit = new RecordingAuditLog();
        var controller = new CompaniesController(scenario.Db, audit);
        var result = await controller.UpdateActivationStatus(
            company.Id,
            new UpdateCompanyActivationRequest { Status = CompanyActivationStatuses.Rejected },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(CompanyActivationStatuses.Rejected, (await scenario.Db.Companies.SingleAsync()).ActivationStatus);
        var persistedActiveUser = await scenario.Db.Users.SingleAsync(user => user.Id == activeUser.Id);
        Assert.False(persistedActiveUser.IsActive);
        Assert.Equal(4, persistedActiveUser.CredentialVersion);
        var persistedInactiveUser = await scenario.Db.Users.SingleAsync(user => user.Id == alreadyInactiveUser.Id);
        Assert.False(persistedInactiveUser.IsActive);
        Assert.Equal(7, persistedInactiveUser.CredentialVersion);
        Assert.Equal(0, await scenario.Db.RefreshSessions.CountAsync());
        Assert.Equal("company.activation_rejected", Assert.Single(audit.Actions));
    }

    [Fact]
    public async Task ApprovingCompanyMakesItActiveWithoutReactivatingPreviouslyDisabledUsers()
    {
        await using var scenario = await CreateScenarioAsync();
        var company = new Company { Name = "Empresa Pendente", ActivationStatus = CompanyActivationStatuses.Pending };
        var user = new User { Name = "Responsável", Email = "responsavel@example.com", CompanyId = company.Id, IsActive = false };
        company.CreatedByUserId = user.Id;
        scenario.Db.Companies.Add(company);
        scenario.Db.Users.Add(user);
        await scenario.Db.SaveChangesAsync();

        var audit = new RecordingAuditLog();
        var controller = new CompaniesController(scenario.Db, audit);
        var result = await controller.UpdateActivationStatus(
            company.Id,
            new UpdateCompanyActivationRequest { Status = CompanyActivationStatuses.Active },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(CompanyActivationStatuses.Active, (await scenario.Db.Companies.SingleAsync()).ActivationStatus);
        var activatedOwner = await scenario.Db.Users.SingleAsync();
        Assert.True(activatedOwner.IsActive);
        Assert.Equal(1, activatedOwner.CredentialVersion);
        Assert.Equal("company.activation_approved", Assert.Single(audit.Actions));
    }

    private static RefreshSession NewRefreshSession(User user) => new()
    {
        UserId = user.Id,
        TokenHash = Guid.NewGuid().ToString("N"),
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(1)
    };

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

    private sealed class RecordingAuditLog : IAuditLogService
    {
        public List<string> Actions { get; } = new();

        public Task RecordAsync(
            string action,
            string resourceType,
            string? resourceId = null,
            Guid? companyId = null,
            object? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }
}
