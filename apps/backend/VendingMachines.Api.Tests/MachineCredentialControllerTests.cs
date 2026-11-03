using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class MachineCredentialControllerTests
{
    [Fact]
    public async Task RotateCredentialDoesNotMutateWhenOnlineOwnerCannotBeRouted()
    {
        await using var scenario = await CreateScenarioAsync();
        var previousHash = (await scenario.Db.Machines.SingleAsync()).TelemetryCredentialHash;
        var previousCreatedAt = (await scenario.Db.Machines.SingleAsync()).TelemetryCredentialCreatedAt;

        var result = await scenario.Controller.RotateTelemetryCredential("machine-credential", CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        Assert.Equal("machine_owner_unknown", response.Value!.GetType().GetProperty("code")!.GetValue(response.Value));
        scenario.Db.ChangeTracker.Clear();
        var machine = await scenario.Db.Machines.SingleAsync();
        Assert.Equal(previousHash, machine.TelemetryCredentialHash);
        Assert.Equal(previousCreatedAt, machine.TelemetryCredentialCreatedAt);
        Assert.Null(machine.TelemetryCredentialRevokedAt);
        Assert.True((await scenario.Db.MachineConnectionStates.SingleAsync()).IsOnline);
        Assert.Equal(0, await scenario.Db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM CredentialMutationAttempts").SingleAsync());
    }

    [Fact]
    public async Task RevokeCredentialDoesNotMutateWhenOnlineOwnerCannotBeRouted()
    {
        await using var scenario = await CreateScenarioAsync();
        var previousHash = (await scenario.Db.Machines.SingleAsync()).TelemetryCredentialHash;
        var previousCreatedAt = (await scenario.Db.Machines.SingleAsync()).TelemetryCredentialCreatedAt;

        var result = await scenario.Controller.RevokeTelemetryCredential("machine-credential", CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        Assert.Equal("machine_owner_unknown", response.Value!.GetType().GetProperty("code")!.GetValue(response.Value));
        scenario.Db.ChangeTracker.Clear();
        var machine = await scenario.Db.Machines.SingleAsync();
        Assert.Equal(previousHash, machine.TelemetryCredentialHash);
        Assert.Equal(previousCreatedAt, machine.TelemetryCredentialCreatedAt);
        Assert.Null(machine.TelemetryCredentialRevokedAt);
        Assert.True((await scenario.Db.MachineConnectionStates.SingleAsync()).IsOnline);
        Assert.Equal(0, await scenario.Db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM CredentialMutationAttempts").SingleAsync());
    }

    [Fact]
    public async Task RotateCredentialStillWorksWhenMachineIsOffline()
    {
        await using var scenario = await CreateScenarioAsync(isOnline: false);

        var result = Assert.IsType<OkObjectResult>(
            await scenario.Controller.RotateTelemetryCredential("machine-credential", CancellationToken.None));

        Assert.NotNull(result.Value!.GetType().GetProperty("credential")!.GetValue(result.Value));
        Assert.Equal(1, await scenario.Db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM CredentialMutationAttempts").SingleAsync());
    }

    [Fact]
    public async Task RevokeCredentialStillWorksWhenMachineIsOffline()
    {
        await using var scenario = await CreateScenarioAsync(isOnline: false);

        var result = await scenario.Controller.RevokeTelemetryCredential("machine-credential", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        scenario.Db.ChangeTracker.Clear();
        Assert.NotNull((await scenario.Db.Machines.SingleAsync()).TelemetryCredentialRevokedAt);
        Assert.Equal(1, await scenario.Db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM CredentialMutationAttempts").SingleAsync());
    }

    [Theory]
    [InlineData(true, "SERIAL-CREDENTIAL-CHANGED")]
    [InlineData(false, "SERIAL-CREDENTIAL")]
    public async Task UpdateCannotChangeSerialOrDeactivateOnlineLegacyOwner(bool isActive, string serialNumber)
    {
        await using var scenario = await CreateScenarioAsync();
        var result = await scenario.Controller.Update(
            "machine-credential",
            new Machine
            {
                Id = "machine-credential",
                Name = "Máquina alterada",
                SerialNumber = serialNumber,
                NormalizedSerialNumber = serialNumber,
                IsActive = isActive
            },
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("machine_in_use", conflict.Value!.GetType().GetProperty("code")!.GetValue(conflict.Value));
        scenario.Db.ChangeTracker.Clear();
        var machine = await scenario.Db.Machines.SingleAsync();
        Assert.Equal("SERIAL-CREDENTIAL", machine.SerialNumber);
        Assert.True(machine.IsActive);
        Assert.Equal(0, await scenario.Db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM MachineIdentityMutationAttempts").SingleAsync());
    }

    private static async Task<CredentialScenario> CreateScenarioAsync(bool isOnline = true)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE CredentialMutationAttempts (Id INTEGER PRIMARY KEY AUTOINCREMENT);");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER track_credential_mutation BEFORE UPDATE OF TelemetryCredentialHash, TelemetryCredentialRevokedAt ON Machines " +
            "BEGIN INSERT INTO CredentialMutationAttempts (Id) VALUES (NULL); END;");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE MachineIdentityMutationAttempts (Id INTEGER PRIMARY KEY AUTOINCREMENT);");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER track_machine_identity_mutation BEFORE UPDATE OF SerialNumber, IsActive ON Machines " +
            "BEGIN INSERT INTO MachineIdentityMutationAttempts (Id) VALUES (NULL); END;");

        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa de credenciais", ActivationStatus = CompanyActivationStatuses.Active };
        var machine = new Machine
        {
            Id = "machine-credential", Name = "Máquina", SerialNumber = "SERIAL-CREDENTIAL",
            NormalizedSerialNumber = "SERIAL-CREDENTIAL", CompanyId = company.Id,
            TelemetryCredentialHash = DeviceCredentialService.Hash(DeviceCredentialService.Generate()),
            TelemetryCredentialCreatedAt = DateTime.UtcNow.AddDays(-1)
        };
        db.Companies.Add(company);
        db.Machines.Add(machine);
        db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = machine.Id,
            CompanyId = company.Id,
            IsOnline = isOnline,
            LastSeenAt = DateTime.UtcNow,
            ConnectionId = "legacy-connection",
            OwnerNodeId = null,
            CommandRoutingVersion = null
        });
        await db.SaveChangesAsync();

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddDbContext<AppDbContext>(builder => builder.UseSqlite(connection))
            .BuildServiceProvider();
        var manager = new TelemetryConnectionManager(
            services.GetRequiredService<IServiceScopeFactory>(),
            new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance),
            NullLogger<TelemetryConnectionManager>.Instance);
        var controller = new MachinesController(new DatabaseDataService(db), db, manager)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("company_id", company.Id.ToString()),
                        new Claim(ClaimTypes.Role, AppRoles.Admin)
                    ], "test"))
                }
            }
        };

        return new CredentialScenario(connection, services, db, manager, controller);
    }

    private sealed class CredentialScenario : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;
        private readonly AppDbContext _db;
        private readonly TelemetryConnectionManager _manager;

        public CredentialScenario(
            SqliteConnection connection,
            ServiceProvider services,
            AppDbContext db,
            TelemetryConnectionManager manager,
            MachinesController controller)
        {
            _connection = connection;
            _services = services;
            _db = db;
            _manager = manager;
            Db = db;
            Controller = controller;
        }

        public AppDbContext Db { get; }
        public MachinesController Controller { get; }

        public async ValueTask DisposeAsync()
        {
            _manager.Dispose();
            await _services.DisposeAsync();
            await _db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
