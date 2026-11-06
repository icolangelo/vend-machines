using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
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

public sealed class LegacyMachineConnectionsControllerTests
{
    [Fact]
    public async Task GetPaginatesOnlyRecentLegacyConnectionsAndReturnsSafeMachineDetails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa de teste", Cnpj = "11111111000111" };
        var legacyA = new Machine { Id = "legacy-a", Name = "Máquina antiga A", SerialNumber = "LEGACY-A", CompanyId = company.Id };
        var legacyB = new Machine { Id = "legacy-b", Name = "Máquina antiga B", SerialNumber = "LEGACY-B", CompanyId = company.Id };
        var modern = new Machine { Id = "modern", Name = "Máquina atual", SerialNumber = "MODERN", CompanyId = company.Id };
        var stale = new Machine { Id = "stale", Name = "Máquina parada", SerialNumber = "STALE", CompanyId = company.Id };
        db.Companies.Add(company);
        db.Machines.AddRange(legacyA, legacyB, modern, stale);
        db.MachineConnectionStates.AddRange(
            LegacyState(legacyA.Id, company.Id, "connection-a", DateTime.UtcNow.AddSeconds(-20)),
            LegacyState(legacyB.Id, company.Id, "connection-b", DateTime.UtcNow.AddSeconds(-10)),
            new MachineConnectionState
            {
                MachineId = modern.Id,
                CompanyId = company.Id,
                IsOnline = true,
                ConnectionId = "connection-modern",
                OwnerNodeId = "candidate-node",
                OwnerConnectionId = "connection-modern",
                CommandRoutingVersion = TelemetryConnectionManager.CurrentCommandRoutingVersion,
                OwnerLeaseExpiresAt = DateTime.UtcNow.AddMinutes(1),
                LastSeenAt = DateTime.UtcNow
            },
            LegacyState(stale.Id, company.Id, "connection-stale", DateTime.UtcNow.AddMinutes(-10)));
        await db.SaveChangesAsync();

        var controller = new LegacyMachineConnectionsController(db, CreateHealthyLegacyRelay());
        var response = Assert.IsType<OkObjectResult>(await controller.Get(page: 1, pageSize: 1));
        var payload = JsonSerializer.SerializeToElement(response.Value);

        Assert.Equal(2, payload.GetProperty("totalItems").GetInt32());
        Assert.Equal(2, payload.GetProperty("totalPages").GetInt32());
        Assert.Equal(1, payload.GetProperty("page").GetInt32());
        Assert.Equal("LEGACY-A", payload.GetProperty("items")[0].GetProperty("serialNumber").GetString());
        Assert.Equal("Máquina antiga A", payload.GetProperty("items")[0].GetProperty("machineName").GetString());
        Assert.Equal("Empresa de teste", payload.GetProperty("items")[0].GetProperty("companyName").GetString());
        Assert.True(payload.GetProperty("relay").GetProperty("configured").GetBoolean());
        Assert.True(payload.GetProperty("relay").GetProperty("healthy").GetBoolean());
        Assert.DoesNotContain("ConnectionId", payload.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("TelemetryCredential", payload.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PayloadJson", payload.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void EndpointIsRestrictedToPlatformAdministrators()
    {
        var authorize = Assert.Single(typeof(LegacyMachineConnectionsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>());

        Assert.Equal(AppRoles.PlatformAdmin, authorize.Roles);
    }

    private static MachineConnectionState LegacyState(string machineId, Guid companyId, string connectionId, DateTime lastSeenAt) => new()
    {
        MachineId = machineId,
        CompanyId = companyId,
        MonitoringEnabled = true,
        IsOnline = true,
        ConnectionId = connectionId,
        LastSeenAt = lastSeenAt
    };

    private static LegacyTelemetryRequestRelay CreateHealthyLegacyRelay()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:LegacyApiBaseUrl"] = "http://legacy-api.internal"
            })
            .Build();
        return new LegacyTelemetryRequestRelay(
            new FixedHttpClientFactory(new HttpClient(new LegacyApiHealthHandler())),
            configuration);
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class LegacyApiHealthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"Healthy\"}")
            });
    }
}
