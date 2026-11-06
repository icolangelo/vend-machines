using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class TelemetryEventsPrivacyTests
{
    [Fact]
    public async Task EventsEndpointOmitsRawPayloadAndFreeTextDetails()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var companyId = Guid.NewGuid();
        db.Companies.Add(new Company { Id = companyId, Name = "Empresa de teste" });
        db.TelemetryEvents.Add(new TelemetryEvent
        {
            CompanyId = companyId,
            MachineId = "MACHINE-1",
            EventType = "vend.request",
            Detail = "private-detail@example.test",
            DataJson = "{\"token\":\"private-payload-token\"}"
        });
        await db.SaveChangesAsync();

        var controller = new TelemetryController(
            db,
            null!,
            null!,
            null!,
            null!,
            null!,
            Options.Create(new MdbStatusOptions()));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("company_id", companyId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
                }, "Test"))
            }
        };

        var response = await controller.GetEvents(null, cancellationToken: CancellationToken.None);
        var json = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(response).Value);

        Assert.Contains("vend.request", json);
        Assert.Contains("MACHINE-1", json);
        Assert.DoesNotContain("private-detail@example.test", json);
        Assert.DoesNotContain("private-payload-token", json);
        Assert.DoesNotContain("DataJson", json);
        Assert.DoesNotContain("Detail", json);
    }
}
