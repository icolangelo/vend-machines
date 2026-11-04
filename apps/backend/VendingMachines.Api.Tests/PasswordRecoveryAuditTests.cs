using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class PasswordRecoveryAuditTests
{
    [Fact]
    public async Task SelfServicePasswordRecoveryIsGenericAndRevokesTokenWhenEmailDeliveryFails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa ativa", ActivationStatus = CompanyActivationStatuses.Active };
        var user = new User
        {
            Id = Guid.NewGuid(), Name = "Operador", Email = "operator@example.com",
            NormalizedEmail = "operator@example.com", Role = AppRoles.Operator,
            CompanyId = company.Id, Company = company, IsActive = true,
            EmailVerifiedAt = DateTime.UtcNow, PasswordHash = "old-password-hash"
        };
        db.Companies.Add(company);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var sender = new RecordingAccountEmailSender(shouldSend: true);
        var controller = new AuthController(db, null!, null!, null!, accountEmailSender: sender);
        var unknownResult = Assert.IsType<AcceptedResult>(await controller.RequestPasswordReset(
            new RequestPasswordResetRequest { Email = "unknown@example.com" }, CancellationToken.None));
        var sentResult = Assert.IsType<AcceptedResult>(await controller.RequestPasswordReset(
            new RequestPasswordResetRequest { Email = " OPERATOR@example.com " }, CancellationToken.None));
        Assert.Equal(unknownResult.Value!.GetType().GetProperty("message")!.GetValue(unknownResult.Value),
            sentResult.Value!.GetType().GetProperty("message")!.GetValue(sentResult.Value));
        Assert.Single(sender.PasswordResets);
        Assert.Null(sentResult.Value.GetType().GetProperty("resetToken"));
        var stored = await db.PasswordResetTokens.SingleAsync();
        Assert.Equal(OneTimeTokens.Hash(sender.PasswordResets[0].Token), stored.TokenHash);
        Assert.Equal(user.Id, stored.CreatedByUserId);

        var failingSender = new RecordingAccountEmailSender(shouldSend: false);
        var failingController = new AuthController(db, null!, null!, null!, accountEmailSender: failingSender);
        var failedDeliveryResult = Assert.IsType<AcceptedResult>(await failingController.RequestPasswordReset(
            new RequestPasswordResetRequest { Email = user.Email }, CancellationToken.None));
        Assert.Equal(unknownResult.Value.GetType().GetProperty("message")!.GetValue(unknownResult.Value),
            failedDeliveryResult.Value!.GetType().GetProperty("message")!.GetValue(failedDeliveryResult.Value));
        var latestToken = await db.PasswordResetTokens.OrderByDescending(token => token.CreatedAt).FirstAsync();
        Assert.NotNull(latestToken.RevokedAt);
    }

    [Fact]
    public async Task AccountLinksAreEmailedWithoutReturningTokensAndFailedDeliveryKeepsManualResetFallback()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa ativa", ActivationStatus = CompanyActivationStatuses.Active };
        var admin = new User
        {
            Id = Guid.NewGuid(), Name = "Admin", Email = "admin@example.com", Role = AppRoles.Admin,
            CompanyId = company.Id, Company = company, IsActive = true
        };
        var target = new User
        {
            Id = Guid.NewGuid(), Name = "Operador", Email = "operator@example.com", Role = AppRoles.Operator,
            CompanyId = company.Id, Company = company, IsActive = true, PasswordHash = "old-password-hash"
        };
        db.Companies.Add(company);
        db.Users.AddRange(admin, target);
        await db.SaveChangesAsync();

        var audit = new RecordingAuditLog();
        var emailSender = new RecordingAccountEmailSender(shouldSend: true);
        var usersController = new UsersController(db, audit, accountEmailSender: emailSender);
        SetUser(usersController, admin.Id, company.Id, AppRoles.Admin);

        var invitationResult = Assert.IsType<OkObjectResult>(await usersController.CreateInvitation(
            new CreateUserInvitationRequest { Email = "new.operator@example.com", Role = AppRoles.Operator },
            CancellationToken.None));
        Assert.Null(invitationResult.Value!.GetType().GetProperty("invitationToken")!.GetValue(invitationResult.Value));
        Assert.True((bool)invitationResult.Value.GetType().GetProperty("emailSent")!.GetValue(invitationResult.Value)!);
        var invitationToken = Assert.Single(emailSender.Invitations).Token;
        Assert.Equal(OneTimeTokens.Hash(invitationToken), (await db.UserInvitations.SingleAsync()).TokenHash);
        Assert.Equal("new.operator@example.com", emailSender.Invitations[0].Recipient);

        var issueResult = Assert.IsType<OkObjectResult>(await usersController.CreatePasswordResetLink(target.Id, CancellationToken.None));
        Assert.True((bool)issueResult.Value!.GetType().GetProperty("emailSent")!.GetValue(issueResult.Value)!);
        Assert.Null(issueResult.Value.GetType().GetProperty("resetToken")!.GetValue(issueResult.Value));
        var rawToken = Assert.Single(emailSender.PasswordResets).Token;
        Assert.Contains("user.password_reset_issued", audit.Actions);

        var storedReset = await db.PasswordResetTokens.SingleAsync();
        Assert.Equal(OneTimeTokens.Hash(rawToken), storedReset.TokenHash);
        Assert.DoesNotContain(rawToken, storedReset.TokenHash, StringComparison.Ordinal);

        var authController = new AuthController(
            db,
            tokenService: null!,
            refreshSessions: null!,
            cookieService: null!,
            audit: audit);
        authController.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        var resetResult = await authController.ResetPassword(new ResetPasswordRequest
        {
            ResetToken = rawToken,
            Password = "nova-senha-segura-123" // gitleaks:allow
        }, CancellationToken.None);

        Assert.IsType<NoContentResult>(resetResult);
        Assert.Contains("auth.password_reset_completed", audit.Actions);
        await db.Entry(storedReset).ReloadAsync();
        Assert.NotEqual("old-password-hash", target.PasswordHash);
        Assert.Equal(1, target.CredentialVersion);
        Assert.NotNull(storedReset.UsedAt);

        var failedEmailSender = new RecordingAccountEmailSender(shouldSend: false);
        var fallbackController = new UsersController(db, audit, accountEmailSender: failedEmailSender);
        SetUser(fallbackController, admin.Id, company.Id, AppRoles.Admin);
        var fallbackResult = Assert.IsType<OkObjectResult>(await fallbackController.CreatePasswordResetLink(target.Id, CancellationToken.None));
        Assert.False((bool)fallbackResult.Value!.GetType().GetProperty("emailSent")!.GetValue(fallbackResult.Value)!);
        var fallbackToken = Assert.IsType<string>(fallbackResult.Value.GetType().GetProperty("resetToken")!.GetValue(fallbackResult.Value));
        Assert.True(await db.PasswordResetTokens.AnyAsync(item => item.TokenHash == OneTimeTokens.Hash(fallbackToken)));
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
                }, "Test"))
            }
        };
    }

    private sealed class RecordingAuditLog : IAuditLogService
    {
        public List<string> Actions { get; } = [];

        public Task RecordAsync(string action, string resourceType, string? resourceId = null,
            Guid? companyId = null, object? metadata = null, CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAccountEmailSender(bool shouldSend) : IAccountEmailSender
    {
        public List<(string Recipient, string Role, string Token)> Invitations { get; } = [];
        public List<(string Recipient, string Token)> PasswordResets { get; } = [];

        public Task<bool> SendEmailVerificationAsync(
            string recipient,
            string token,
            DateTime expiresAt,
            CancellationToken cancellationToken) => Task.FromResult(shouldSend);

        public Task<bool> SendInvitationAsync(
            string recipient,
            string role,
            string token,
            DateTime expiresAt,
            CancellationToken cancellationToken)
        {
            Invitations.Add((recipient, role, token));
            return Task.FromResult(shouldSend);
        }

        public Task<bool> SendPasswordResetAsync(
            string recipient,
            string token,
            DateTime expiresAt,
            CancellationToken cancellationToken)
        {
            PasswordResets.Add((recipient, token));
            return Task.FromResult(shouldSend);
        }
    }
}
