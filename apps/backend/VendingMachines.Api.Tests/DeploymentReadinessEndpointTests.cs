using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class DeploymentReadinessEndpointTests
{
    [Fact]
    public async Task DeploymentReadinessDoesNotRequirePausingNewSessionAdmission()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.SessionAdmissionGates.Add(new VendingMachines.Api.Models.SessionAdmissionGate
        {
            Id = VendingMachines.Api.Models.SessionAdmissionGate.SingletonId,
            NewSessionsPaused = false,
            UpdatedBy = "test",
            Reason = "endpoint test"
        });
        db.SystemSettings.Add(new VendingMachines.Api.Models.SystemSettings { ApplicationFeePercent = 5m });
        await db.SaveChangesAsync();

        var readiness = new DeploymentReadinessService(db);
        using var lifetime = new ApiReadinessEndpointTests.TestApplicationLifetime();
        var result = await DeploymentReadinessEndpoint.HandleAsync(readiness, lifetime, CancellationToken.None);
        var context = await ApiReadinessEndpointTests.ExecuteResultAsync(result);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("Ready", await ApiReadinessEndpointTests.GetJsonStringAsync(context, "status"));
    }

    [Fact]
    public async Task DeploymentReadinessReturns503WhenOnlineMachineHasLegacyOwner()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.SessionAdmissionGates.Add(new SessionAdmissionGate
        {
            Id = SessionAdmissionGate.SingletonId,
            NewSessionsPaused = true,
            UpdatedBy = "test",
            Reason = "legacy owner endpoint test"
        });
        db.SystemSettings.Add(new SystemSettings { ApplicationFeePercent = 5m });
        var company = new Company { Name = "Empresa de teste", ActivationStatus = CompanyActivationStatuses.Active };
        var machine = new Machine
        {
            Id = "deploy-ready-legacy-machine",
            Name = "Máquina legada",
            SerialNumber = "DEPLOY-READY-LEGACY",
            NormalizedSerialNumber = "DEPLOY-READY-LEGACY",
            CompanyId = company.Id
        };
        db.Companies.Add(company);
        db.Machines.Add(machine);
        await db.SaveChangesAsync();
        db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = machine.Id,
            CompanyId = company.Id,
            IsOnline = true,
            ConnectionId = "legacy-owner-connection",
            LastSeenAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var readiness = new DeploymentReadinessService(db);
        using var lifetime = new ApiReadinessEndpointTests.TestApplicationLifetime();
        var result = await DeploymentReadinessEndpoint.HandleAsync(readiness, lifetime, CancellationToken.None);
        var context = await ApiReadinessEndpointTests.ExecuteResultAsync(result);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("NotReady", await ApiReadinessEndpointTests.GetJsonStringAsync(context, "status"));
        Assert.Equal("legacy_owner_api_unavailable", await ApiReadinessEndpointTests.GetJsonStringAsync(context, "reason"));
    }

    [Fact]
    public async Task DatabaseFailureReturnsGenericNotReadyResponseWithoutDetails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.SessionAdmissionGates.Add(new SessionAdmissionGate
        {
            Id = SessionAdmissionGate.SingletonId,
            NewSessionsPaused = true,
            UpdatedBy = "test",
            Reason = "endpoint test"
        });
        db.SystemSettings.Add(new SystemSettings { ApplicationFeePercent = 5m });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"MachineSessions\"");

        var readiness = new DeploymentReadinessService(db);
        using var lifetime = new ApiReadinessEndpointTests.TestApplicationLifetime();
        var result = await DeploymentReadinessEndpoint.HandleAsync(readiness, lifetime, CancellationToken.None);
        await using var body = new MemoryStream();
        var context = new DefaultHttpContext();
        using var responseServices = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        context.RequestServices = responseServices;
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body);
        Assert.Equal("NotReady", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("deployment_preflight_failed", json.RootElement.GetProperty("reason").GetString());
        Assert.DoesNotContain("MachineSessions", await ReadBodyAsync(context.Response.Body));
    }

    [Fact]
    public async Task DeploymentReadinessFailsWhenShutdownStartsWithoutQueryingDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        var readiness = new DeploymentReadinessService(db);
        using var lifetime = new ApiReadinessEndpointTests.TestApplicationLifetime();
        lifetime.BeginStopping();

        var result = await DeploymentReadinessEndpoint.HandleAsync(readiness, lifetime, CancellationToken.None);
        var context = await ApiReadinessEndpointTests.ExecuteResultAsync(result);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("server_shutting_down", await ApiReadinessEndpointTests.GetJsonStringAsync(context, "reason"));
    }

    private static async Task<string> ReadBodyAsync(Stream body)
    {
        body.Position = 0;
        using var reader = new StreamReader(body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
