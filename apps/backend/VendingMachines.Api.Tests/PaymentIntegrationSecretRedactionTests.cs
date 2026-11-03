using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class PaymentIntegrationSecretRedactionTests
{
    [Fact]
    public async Task CompanyIntegrationResponseDoesNotExposeAnyPartOfCredentials()
    {
        await using var scenario = await CreateScenarioAsync();
        var company = AddIntegration(scenario.Db);
        await scenario.Db.SaveChangesAsync();

        var controller = new PaymentsController(
            scenario.Db,
            sessionOrchestrator: null!,
            scopeFactory: null!,
            configuration: new ConfigurationBuilder().Build(),
            environment: null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("company_id", company.Id.ToString()),
                        new Claim(ClaimTypes.Role, AppRoles.Admin)
                    ], "Test"))
                }
            }
        };

        var result = Assert.IsType<OkObjectResult>(await controller.GetIntegration());
        AssertNoCredentialFragments(result.Value, hasAccessToken: true, hasClientSecret: true);
    }

    [Fact]
    public async Task PlatformAdminCompanyIntegrationResponseDoesNotExposeAnyPartOfCredentials()
    {
        await using var scenario = await CreateScenarioAsync();
        var company = AddIntegration(scenario.Db);
        await scenario.Db.SaveChangesAsync();

        var controller = new CompaniesController(scenario.Db);
        var result = Assert.IsType<OkObjectResult>(await controller.GetCompanyIntegration(company.Id));

        AssertNoCredentialFragments(result.Value, hasAccessToken: true, hasClientSecret: true);
    }

    private static Company AddIntegration(AppDbContext db)
    {
        var company = new Company
        {
            Id = Guid.NewGuid(),
            Name = "Empresa sintética",
            Cnpj = "00000000000100",
            NormalizedCnpj = "00000000000100"
        };
        db.Companies.Add(company);
        db.MercadoPagoIntegrations.Add(new MercadoPagoIntegration
        {
            CompanyId = company.Id,
            AccessToken = "APP_USR-SENSITIVE-ACCESS-TOKEN-123456789",
            RefreshToken = "REFRESH-SENSITIVE-TOKEN-987654321",
            ClientSecret = "SENSITIVE-CLIENT-SECRET-123456789",
            IsActive = true
        });
        return company;
    }

    private static void AssertNoCredentialFragments(object? value, bool hasAccessToken, bool hasClientSecret)
    {
        var dto = Assert.IsType<MercadoPagoIntegrationDto>(value);
        Assert.Equal(string.Empty, dto.AccessToken);
        Assert.Equal(string.Empty, dto.ClientSecret);
        Assert.Equal(hasAccessToken, dto.HasAccessToken);
        Assert.Equal(hasClientSecret, dto.HasClientSecret);
        Assert.True(dto.HasRefreshToken);

        var serialized = System.Text.Json.JsonSerializer.Serialize(dto);
        Assert.DoesNotContain("APP_USR-SENSITIVE-ACCESS-TOKEN", serialized);
        Assert.DoesNotContain("SENSITIVE-CLIENT-SECRET", serialized);
        Assert.DoesNotContain("REFRESH-SENSITIVE-TOKEN", serialized);
        Assert.DoesNotContain("TokenFingerprint", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fingerprint", serialized, StringComparison.OrdinalIgnoreCase);
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
