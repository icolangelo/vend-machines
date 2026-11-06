using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class AuthControllerSessionFlowTests
{
    [Fact]
    public async Task RefreshWithInvalidCookieRejectsAndExpiresTheCookie()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var authOptions = Options.Create(new AuthOptions { RefreshTokenDays = 30, RefreshReuseGraceSeconds = 10 });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "AuthControllerSessionFlowTestSigningKey-2026!",
            ["Jwt:Issuer"] = "Tests",
            ["Jwt:Audience"] = "Tests"
        }).Build();
        var cookieService = new AuthCookieService(authOptions, new TestEnvironment());
        var context = CreateHttpsContext("invalid-refresh-token", cookieService.CookieName);
        var controller = CreateController(
            db,
            new AuthTokenService(configuration, authOptions, TimeProvider.System),
            new RefreshSessionService(db, authOptions, TimeProvider.System, NullLogger<RefreshSessionService>.Instance),
            cookieService,
            context);

        var result = Assert.IsType<UnauthorizedObjectResult>(await controller.Refresh(CancellationToken.None));

        Assert.Equal(0, await db.RefreshSessions.CountAsync());
        Assert.Contains("session_expired", JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
        var expiredCookie = Assert.Single(context.Response.Headers.SetCookie);
        Assert.Contains($"{cookieService.CookieName}=", expiredCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", expiredCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", expiredCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=Thu, 01 Jan 1970", expiredCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoginRefreshAndLogoutKeepRefreshTokenInSecureHttpOnlyCookie()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Name = "Administradora",
            Email = "admin@example.com",
            NormalizedEmail = "admin@example.com",
            PasswordHash = string.Empty,
            Role = AppRoles.PlatformAdmin,
            IsActive = true,
            Cpf = "12345678901",
            AcceptedPrivacyPolicy = true,
            CreatedAt = DateTime.UtcNow
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "senha-de-teste-segura-123");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var authOptions = Options.Create(new AuthOptions
        {
            AccessTokenMinutes = 15,
            RefreshTokenDays = 30,
            RefreshReuseGraceSeconds = 10
        });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "AuthControllerSessionFlowTestSigningKey-2026!",
            ["Jwt:Issuer"] = "Tests",
            ["Jwt:Audience"] = "Tests"
        }).Build();
        var tokenService = new AuthTokenService(configuration, authOptions, TimeProvider.System);
        var refreshService = new RefreshSessionService(
            db,
            authOptions,
            TimeProvider.System,
            NullLogger<RefreshSessionService>.Instance);
        var cookieService = new AuthCookieService(authOptions, new TestEnvironment());
        var cookieName = cookieService.CookieName;

        var loginHttp = CreateHttpsContext();
        var loginController = CreateController(db, tokenService, refreshService, cookieService, loginHttp);
        var loginResult = Assert.IsType<OkObjectResult>(await loginController.Login(new LoginRequest
        {
            Email = user.Email,
            Password = "senha-de-teste-segura-123" // gitleaks:allow
        }));

        var firstCookie = AssertCookie(loginHttp, cookieName);
        Assert.DoesNotContain(firstCookie, JsonSerializer.Serialize(loginResult.Value), StringComparison.Ordinal);
        Assert.Equal(1, await db.RefreshSessions.CountAsync());
        var initialSession = await db.RefreshSessions.SingleAsync();
        Assert.NotEqual(firstCookie, initialSession.TokenHash);

        var refreshHttp = CreateHttpsContext(firstCookie, cookieName);
        var refreshController = CreateController(db, tokenService, refreshService, cookieService, refreshHttp);
        var refreshResult = Assert.IsType<OkObjectResult>(await refreshController.Refresh(CancellationToken.None));
        var rotatedCookie = AssertCookie(refreshHttp, cookieName);

        Assert.NotEqual(firstCookie, rotatedCookie);
        Assert.DoesNotContain(firstCookie, JsonSerializer.Serialize(refreshResult.Value), StringComparison.Ordinal);
        Assert.DoesNotContain(rotatedCookie, JsonSerializer.Serialize(refreshResult.Value), StringComparison.Ordinal);
        Assert.Equal(1, await db.RefreshSessions.CountAsync(session => session.RevokedAt == null));
        Assert.NotNull((await db.RefreshSessions.AsNoTracking()
            .SingleAsync(session => session.TokenHash == initialSession.TokenHash)).RevokedAt);

        var logoutHttp = CreateHttpsContext(rotatedCookie, cookieName);
        var logoutController = CreateController(db, tokenService, refreshService, cookieService, logoutHttp);
        Assert.IsType<NoContentResult>(await logoutController.Logout(CancellationToken.None));

        Assert.Equal(0, await db.RefreshSessions.CountAsync(session => session.RevokedAt == null));
        var logoutCookie = Assert.Single(logoutHttp.Response.Headers.SetCookie);
        Assert.Contains($"{cookieName}=", logoutCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", logoutCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", logoutCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=Thu, 01 Jan 1970", logoutCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcceptingInvitationRecordsTheCurrentPrivacyPolicyVersion()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        const string invitationToken = "single-use-invitation-token";
        var company = new Company { Name = "Empresa Ativa", ActivationStatus = CompanyActivationStatuses.Active };
        db.Companies.Add(company);
        db.UserInvitations.Add(new UserInvitation
        {
            CompanyId = company.Id,
            CreatedByUserId = Guid.NewGuid(),
            Email = "operator@example.com",
            Role = AppRoles.Operator,
            TokenHash = OneTimeTokens.Hash(invitationToken),
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var authOptions = Options.Create(new AuthOptions { RefreshTokenDays = 30, RefreshReuseGraceSeconds = 10 });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "AuthControllerSessionFlowTestSigningKey-2026!",
            ["Jwt:Issuer"] = "Tests",
            ["Jwt:Audience"] = "Tests"
        }).Build();
        var tokenService = new AuthTokenService(configuration, authOptions, TimeProvider.System);
        var refreshService = new RefreshSessionService(
            db,
            authOptions,
            TimeProvider.System,
            NullLogger<RefreshSessionService>.Instance);
        var cookieService = new AuthCookieService(authOptions, new TestEnvironment());
        var controller = CreateController(db, tokenService, refreshService, cookieService, CreateHttpsContext());

        var result = await controller.AcceptInvitation(new AcceptInvitationRequest
        {
            InvitationToken = invitationToken,
            Name = "Operador",
            Cpf = "529.982.247-25",
            Password = "senha-segura-com-12", // gitleaks:allow
            AcceptedPrivacyPolicy = true
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var user = await db.Users.SingleAsync();
        Assert.True(user.AcceptedPrivacyPolicy);
        Assert.NotNull(user.PrivacyPolicyAcceptedAt);
        Assert.Equal(PrivacyPolicyVersions.Current, user.PrivacyPolicyVersion);
    }

    private static AuthController CreateController(
        AppDbContext db,
        AuthTokenService tokenService,
        RefreshSessionService refreshService,
        AuthCookieService cookieService,
        HttpContext httpContext)
    {
        var controller = new AuthController(db, tokenService, refreshService, cookieService)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
        return controller;
    }

    private static DefaultHttpContext CreateHttpsContext(string? refreshToken = null, string cookieName = "__Host-vm_refresh")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        context.Request.Headers.UserAgent = "AuthControllerSessionFlowTests";
        if (refreshToken != null)
            context.Request.Headers.Cookie = $"{cookieName}={refreshToken}";
        return context;
    }

    private static string AssertCookie(HttpContext context, string cookieName)
    {
        var cookie = Assert.Single(context.Response.Headers.SetCookie);
        Assert.NotNull(cookie);
        Assert.Contains($"{cookieName}=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        var tokenPart = cookie.Split(';', 2)[0];
        return tokenPart[(tokenPart.IndexOf('=') + 1)..];
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "VendingMachines.Api.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
