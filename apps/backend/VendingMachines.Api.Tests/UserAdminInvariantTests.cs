using System.Security.Claims;
using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class UserAdminInvariantTests
{
    [Fact]
    public async Task PlatformAdminCannotDemoteTheLastActiveCompanyAdmin()
    {
        await using var scenario = await CreateScenarioAsync();
        var user = await AddSingleAdminAsync(scenario.Db);
        var controller = CreateController(scenario.Db, Guid.NewGuid());

        var result = await controller.UpdateRole(
            user.Id,
            new UpdateUserRoleRequest { Role = AppRoles.Operator },
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        var persisted = await scenario.Db.Users.SingleAsync();
        Assert.Equal(AppRoles.Admin, persisted.Role);
        Assert.True(persisted.IsActive);
    }

    [Fact]
    public async Task PlatformAdminCannotDeactivateTheLastActiveCompanyAdmin()
    {
        await using var scenario = await CreateScenarioAsync();
        var user = await AddSingleAdminAsync(scenario.Db);
        var controller = CreateController(scenario.Db, Guid.NewGuid());

        var result = await controller.UpdateActiveStatus(
            user.Id,
            new UpdateUserActiveStatusRequest { IsActive = false },
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        var persisted = await scenario.Db.Users.SingleAsync();
        Assert.Equal(AppRoles.Admin, persisted.Role);
        Assert.True(persisted.IsActive);
    }

    [Fact]
    public async Task PlatformAdminCanDemoteAnAdminWhenAnotherActiveAdminRemains()
    {
        await using var scenario = await CreateScenarioAsync();
        var target = await AddSingleAdminAsync(scenario.Db);
        var remainingAdmin = await AddAnotherAdminAsync(scenario.Db, target.CompanyId!.Value);
        var controller = CreateController(scenario.Db, Guid.NewGuid());

        var result = await controller.UpdateRole(
            target.Id,
            new UpdateUserRoleRequest { Role = AppRoles.Operator },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(AppRoles.Operator, (await scenario.Db.Users.SingleAsync(user => user.Id == target.Id)).Role);
        Assert.True((await scenario.Db.Users.SingleAsync(user => user.Id == remainingAdmin.Id)).IsActive);
        Assert.Equal(AppRoles.Admin, (await scenario.Db.Users.SingleAsync(user => user.Id == remainingAdmin.Id)).Role);
    }

    [Fact]
    public async Task PlatformAdminCanDeactivateAnAdminWhenAnotherActiveAdminRemains()
    {
        await using var scenario = await CreateScenarioAsync();
        var target = await AddSingleAdminAsync(scenario.Db);
        var remainingAdmin = await AddAnotherAdminAsync(scenario.Db, target.CompanyId!.Value);
        var controller = CreateController(scenario.Db, Guid.NewGuid());

        var result = await controller.UpdateActiveStatus(
            target.Id,
            new UpdateUserActiveStatusRequest { IsActive = false },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.False((await scenario.Db.Users.SingleAsync(user => user.Id == target.Id)).IsActive);
        var persistedAdmin = await scenario.Db.Users.SingleAsync(user => user.Id == remainingAdmin.Id);
        Assert.True(persistedAdmin.IsActive);
        Assert.Equal(AppRoles.Admin, persistedAdmin.Role);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentAdminChangesKeepOneActiveAdminWhenPostgresIsConfigured(bool demoteInsteadOfDeactivate)
    {
        var connectionString = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("O teste concorrente de administradores exige VENDMACHINES_TEST_POSTGRES no CI.");
            }

            return;
        }

        var databaseName = $"user_admin_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        var adminConnectionString = adminBuilder.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var scopedConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString;
        try
        {
            Guid[] userIds;
            await using (var setup = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                             .UseNpgsql(scopedConnectionString)
                             .Options))
            {
                await setup.Database.EnsureCreatedAsync();
                var company = new Company { Name = "Empresa concorrente", ActivationStatus = CompanyActivationStatuses.Active };
                setup.Companies.Add(company);
                setup.Users.AddRange(
                    CreateAdmin(company.Id, "admin1@example.com"),
                    CreateAdmin(company.Id, "admin2@example.com"));
                await setup.SaveChangesAsync();
                userIds = await setup.Users.OrderBy(user => user.Email).Select(user => user.Id).ToArrayAsync();
            }

            var barrier = new InitialUserReadBarrier();
            await using var firstDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(scopedConnectionString)
                .AddInterceptors(barrier)
                .Options);
            await using var secondDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(scopedConnectionString)
                .AddInterceptors(barrier)
                .Options);
            var firstController = CreateController(firstDb, Guid.NewGuid());
            var secondController = CreateController(secondDb, Guid.NewGuid());

            var first = demoteInsteadOfDeactivate
                ? firstController.UpdateRole(
                    userIds[0], new UpdateUserRoleRequest { Role = AppRoles.Operator }, CancellationToken.None)
                : firstController.UpdateActiveStatus(
                    userIds[0], new UpdateUserActiveStatusRequest { IsActive = false }, CancellationToken.None);
            var second = demoteInsteadOfDeactivate
                ? secondController.UpdateRole(
                    userIds[1], new UpdateUserRoleRequest { Role = AppRoles.Operator }, CancellationToken.None)
                : secondController.UpdateActiveStatus(
                    userIds[1], new UpdateUserActiveStatusRequest { IsActive = false }, CancellationToken.None);
            var results = await Task.WhenAll(first, second);

            Assert.Single(results.OfType<OkObjectResult>());
            Assert.Single(results.OfType<ConflictObjectResult>());
            Assert.Equal(1, await firstDb.Users.CountAsync(user => user.Role == AppRoles.Admin && user.IsActive));
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static UsersController CreateController(AppDbContext db, Guid actorId)
    {
        var controller = new UsersController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, actorId.ToString()),
                    new Claim(ClaimTypes.Role, AppRoles.PlatformAdmin)
                ], "test"))
            }
        };
        return controller;
    }

    private static async Task<User> AddSingleAdminAsync(AppDbContext db)
    {
        var company = new Company { Name = "Empresa de teste", ActivationStatus = CompanyActivationStatuses.Active };
        var user = new User
        {
            Name = "Administradora",
            Email = "admin@example.com",
            NormalizedEmail = "ADMIN@EXAMPLE.COM",
            Role = AppRoles.Admin,
            CompanyId = company.Id,
            IsActive = true
        };
        db.Companies.Add(company);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<User> AddAnotherAdminAsync(AppDbContext db, Guid companyId)
    {
        var user = new User
        {
            Name = "Segundo administrador",
            Email = "admin2@example.com",
            NormalizedEmail = "ADMIN2@EXAMPLE.COM",
            Role = AppRoles.Admin,
            CompanyId = companyId,
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static User CreateAdmin(Guid companyId, string email) => new()
    {
        Name = "Administradora",
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        Role = AppRoles.Admin,
        CompanyId = companyId,
        IsActive = true
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

    private sealed class InitialUserReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothRequestsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Users\"", StringComparison.Ordinal) &&
                Interlocked.Increment(ref _reads) <= 2)
            {
                if (Volatile.Read(ref _reads) == 2)
                    _bothRequestsReady.TrySetResult();
                await _bothRequestsReady.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
