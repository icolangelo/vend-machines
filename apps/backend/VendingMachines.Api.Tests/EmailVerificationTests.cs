using System.Security.Claims;
using System.Text.Json;
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

public sealed class EmailVerificationTests
{
    [Fact]
    public async Task RegistrationRequiresVerificationEvenIfCompanyIsApprovedFirst()
    {
        await using var scenario = await CreateScenarioAsync();
        var emailSender = new RecordingAccountEmailSender();
        var auth = CreateAuthController(scenario.Db, emailSender);

        var registration = Assert.IsType<AcceptedResult>(await auth.Register(new RegisterRequest
        {
            Name = "Pessoa responsável",
            Email = "responsavel@example.com",
            Password = "senha-segura-com-12", // gitleaks:allow
            Cpf = "529.982.247-25",
            CompanyName = "Empresa Nova",
            CompanyCnpj = "12.345.678/0001-95",
            AcceptedPrivacyPolicy = true
        }));

        Assert.True((bool)registration.Value!.GetType().GetProperty("verificationEmailSent")!.GetValue(registration.Value)!);
        var company = await scenario.Db.Companies.SingleAsync();
        var owner = await scenario.Db.Users.SingleAsync();
        var firstToken = Assert.Single(emailSender.VerificationTokens).Token;
        Assert.DoesNotContain(firstToken, JsonSerializer.Serialize(registration.Value), StringComparison.Ordinal);
        Assert.True(owner.RequiresEmailVerification);
        Assert.Null(owner.EmailVerifiedAt);
        Assert.Equal(OneTimeTokens.Hash(firstToken), owner.EmailVerificationTokenHash);

        var existingAdmin = new User
        {
            Name = "Admin da plataforma cliente",
            Email = "admin-existing@example.com",
            NormalizedEmail = "admin-existing@example.com",
            Role = AppRoles.Admin,
            CompanyId = company.Id,
            IsActive = true
        };
        scenario.Db.Users.Add(existingAdmin);
        await scenario.Db.SaveChangesAsync();

        var usersController = new UsersController(scenario.Db);
        SetUser(usersController, existingAdmin.Id, company.Id, AppRoles.Admin);
        var manualActivation = await usersController.UpdateActiveStatus(
            owner.Id,
            new UpdateUserActiveStatusRequest { IsActive = true },
            CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(manualActivation);

        var companyController = new CompaniesController(scenario.Db);
        var approval = await companyController.UpdateActivationStatus(
            company.Id,
            new UpdateCompanyActivationRequest { Status = CompanyActivationStatuses.Active },
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(approval);
        Assert.False((await scenario.Db.Users.SingleAsync(user => user.Id == owner.Id)).IsActive);

        var resent = Assert.IsType<AcceptedResult>(await auth.ResendEmailVerification(
            new ResendEmailVerificationRequest { Email = owner.Email },
            CancellationToken.None));
        Assert.NotNull(resent.Value);
        var currentToken = emailSender.VerificationTokens.Last().Token;
        Assert.NotEqual(firstToken, currentToken);
        Assert.DoesNotContain(currentToken, JsonSerializer.Serialize(resent.Value), StringComparison.Ordinal);

        var unknownAddressResend = Assert.IsType<AcceptedResult>(await auth.ResendEmailVerification(
            new ResendEmailVerificationRequest { Email = "unknown@example.com" },
            CancellationToken.None));
        Assert.Equal(
            resent.Value!.GetType().GetProperty("message")!.GetValue(resent.Value),
            unknownAddressResend.Value!.GetType().GetProperty("message")!.GetValue(unknownAddressResend.Value));

        var oldTokenResult = await auth.VerifyEmail(new VerifyEmailRequest { Token = firstToken }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(oldTokenResult);

        var verifiedResult = Assert.IsType<OkObjectResult>(await auth.VerifyEmail(
            new VerifyEmailRequest { Token = currentToken },
            CancellationToken.None));
        Assert.True((bool)verifiedResult.Value!.GetType().GetProperty("emailVerified")!.GetValue(verifiedResult.Value)!);
        var verifiedOwner = await scenario.Db.Users.SingleAsync(user => user.Id == owner.Id);
        Assert.NotNull(verifiedOwner.EmailVerifiedAt);
        Assert.Null(verifiedOwner.EmailVerificationTokenHash);
        Assert.Null(verifiedOwner.EmailVerificationTokenExpiresAt);
        Assert.True(verifiedOwner.IsActive);
        Assert.Equal(1, verifiedOwner.CredentialVersion);

        var replay = await auth.VerifyEmail(new VerifyEmailRequest { Token = currentToken }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(replay);
    }

    [Fact]
    public async Task FailedVerificationEmailCanBeRetriedWithoutReturningTokenToPublicCaller()
    {
        await using var scenario = await CreateScenarioAsync();
        var emailSender = new RecordingAccountEmailSender { ShouldSendVerification = false };
        var auth = CreateAuthController(scenario.Db, emailSender);

        var registration = Assert.IsType<AcceptedResult>(await auth.Register(new RegisterRequest
        {
            Name = "Pessoa responsável",
            Email = "responsavel@example.com",
            Password = "senha-segura-com-12", // gitleaks:allow
            Cpf = "529.982.247-25",
            CompanyName = "Empresa Nova",
            CompanyCnpj = "12.345.678/0001-95",
            AcceptedPrivacyPolicy = true
        }));

        Assert.False((bool)registration.Value!.GetType().GetProperty("verificationEmailSent")!.GetValue(registration.Value)!);
        var firstToken = Assert.Single(emailSender.VerificationTokens).Token;
        Assert.DoesNotContain(firstToken, JsonSerializer.Serialize(registration.Value), StringComparison.Ordinal);

        var user = await scenario.Db.Users.SingleAsync();
        Assert.Equal(OneTimeTokens.Hash(firstToken), user.EmailVerificationTokenHash);
        var resend = Assert.IsType<AcceptedResult>(await auth.ResendEmailVerification(
            new ResendEmailVerificationRequest { Email = user.Email },
            CancellationToken.None));

        var retryToken = emailSender.VerificationTokens.Last().Token;
        Assert.NotEqual(firstToken, retryToken);
        Assert.DoesNotContain(retryToken, JsonSerializer.Serialize(resend.Value), StringComparison.Ordinal);
        Assert.Equal(OneTimeTokens.Hash(retryToken), user.EmailVerificationTokenHash);
        Assert.Equal(
            "Se houver um cadastro aguardando confirmação, enviaremos um novo link para o endereço informado.",
            resend.Value!.GetType().GetProperty("message")!.GetValue(resend.Value));
    }

    [Fact]
    public async Task ExistingAccountsDoNotRequireEmailVerificationByDefault()
    {
        await using var scenario = await CreateScenarioAsync();
        var company = new Company { Name = "Empresa existente", ActivationStatus = CompanyActivationStatuses.Active };
        var user = new User
        {
            Name = "Conta existente",
            Email = "existing@example.com",
            NormalizedEmail = "existing@example.com",
            Role = AppRoles.Admin,
            CompanyId = company.Id,
            IsActive = true
        };
        scenario.Db.Companies.Add(company);
        scenario.Db.Users.Add(user);
        await scenario.Db.SaveChangesAsync();

        Assert.False(user.RequiresEmailVerification);
        Assert.Null(user.EmailVerifiedAt);
    }

    [Fact]
    public async Task UnverifiedAccountCannotLogInOrUseAnExistingAccessToken()
    {
        await using var scenario = await CreateScenarioAsync();
        var company = new Company { Name = "Empresa ativa", ActivationStatus = CompanyActivationStatuses.Active };
        var user = new User
        {
            Name = "Responsável pendente",
            Email = "unverified@example.com",
            NormalizedEmail = "unverified@example.com",
            Role = AppRoles.Admin,
            CompanyId = company.Id,
            IsActive = true,
            RequiresEmailVerification = true,
            CredentialVersion = 4
        };
        user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>()
            .HashPassword(user, "senha-segura-com-12");
        scenario.Db.Companies.Add(company);
        scenario.Db.Users.Add(user);
        await scenario.Db.SaveChangesAsync();

        var auth = new AuthController(scenario.Db, null!, null!, null!);
        var login = await auth.Login(new LoginRequest
        {
            Email = user.Email,
            Password = "senha-segura-com-12" // gitleaks:allow
        });
        Assert.IsType<Microsoft.AspNetCore.Mvc.UnauthorizedObjectResult>(login);

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Role, AppRoles.Admin),
                new Claim("credential_version", user.CredentialVersion.ToString())
            }, "test"))
        };
        var nextCalled = false;
        var middleware = new VendingMachines.Api.Middlewares.UserRoleValidationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, scenario.Db);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    private static AuthController CreateAuthController(AppDbContext db, IAccountEmailSender emailSender)
    {
        var controller = new AuthController(db, null!, null!, null!, accountEmailSender: emailSender);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static void SetUser(ControllerBase controller, Guid userId, Guid companyId, string role)
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim("company_id", companyId.ToString()),
                    new Claim(ClaimTypes.Role, role)
                }, "test"))
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

    private sealed class TestScenario(SqliteConnection connection, AppDbContext db) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class RecordingAccountEmailSender : IAccountEmailSender
    {
        public List<(string Recipient, string Token)> VerificationTokens { get; } = [];
        public bool ShouldSendVerification { get; init; } = true;

        public Task<bool> SendEmailVerificationAsync(string recipient, string token, DateTime expiresAt, CancellationToken cancellationToken)
        {
            VerificationTokens.Add((recipient, token));
            return Task.FromResult(ShouldSendVerification);
        }

        public Task<bool> SendInvitationAsync(string recipient, string role, string token, DateTime expiresAt, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> SendPasswordResetAsync(string recipient, string token, DateTime expiresAt, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
