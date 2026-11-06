using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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

public sealed class ClientsControllerImpersonationTests
{
    [Fact]
    public async Task LegacyClientsRouteUsesImpersonatedCompany()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var platformCompany = new Company
        {
            Id = Guid.NewGuid(), Name = "Empresa do administrador",
            Cnpj = "00000000000100", NormalizedCnpj = "00000000000100"
        };
        var selectedCompany = new Company
        {
            Id = Guid.NewGuid(), Name = "Empresa selecionada",
            Cnpj = "00000000000200", NormalizedCnpj = "00000000000200"
        };
        db.Companies.AddRange(platformCompany, selectedCompany);
        db.Locations.AddRange(
            new Location { CompanyId = platformCompany.Id, Name = "Local do administrador" },
            new Location { CompanyId = selectedCompany.Id, Name = "Local selecionado" });
        await db.SaveChangesAsync();

        var controller = new ClientsController(new DatabaseDataService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("company_id", platformCompany.Id.ToString()),
                        new Claim(ClaimTypes.Role, AppRoles.PlatformAdmin),
                        new Claim(ImpersonationMiddleware.ImpersonatedCompanyIdClaim, selectedCompany.Id.ToString())
                    ], "Test"))
                }
            }
        };

        var result = Assert.IsType<OkObjectResult>(controller.GetAll().Result);
        var json = JsonSerializer.Serialize(result.Value);

        Assert.Contains("Local selecionado", json);
        Assert.DoesNotContain("Local do administrador", json);
    }
}
