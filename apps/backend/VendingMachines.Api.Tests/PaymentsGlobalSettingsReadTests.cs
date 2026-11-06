using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class PaymentsGlobalSettingsReadTests
{
    [Fact]
    public async Task ConcurrentConditionalFeeUpdatesInPostgresOnlyOneSucceeds()
    {
        var configured = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste concorrente da taxa exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }
        var databaseName = $"fee_race_{Guid.NewGuid():N}";
        var adminString = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", Pooling = false }.ConnectionString;
        var testString = new NpgsqlConnectionStringBuilder(configured) { Database = databaseName, Pooling = false }.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(testString).Options;
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.SystemSettings.Add(new SystemSettings { ApplicationFeePercent = 5m });
                await setup.SaveChangesAsync();
            }

            await using var locker = new NpgsqlConnection(testString);
            await locker.OpenAsync();
            await using var transaction = await locker.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand("SELECT \"Id\" FROM \"SystemSettings\" FOR UPDATE", locker, transaction))
                Assert.NotNull(await command.ExecuteScalarAsync());

            async Task<IActionResult> Update(decimal fee, string expected = "5")
            {
                await using var db = new AppDbContext(options);
                var controller = CreatePlatformAdminController(db);
                controller.Request.Headers["X-Expected-Platform-Fee"] = expected;
                return await controller.UpdateGlobalSettings(new SystemSettings { ApplicationFeePercent = fee });
            }
            var first = Update(7m);
            var second = Update(9m);
            var waiting = 0;
            await using (var observer = new NpgsqlConnection(testString))
            {
                await observer.OpenAsync();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && !first.IsCompleted && !second.IsCompleted)
                {
                    await using var query = new NpgsqlCommand("SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query ILIKE '%SystemSettings%FOR UPDATE%'", observer);
                    waiting = Convert.ToInt32(await query.ExecuteScalarAsync());
                    if (waiting >= 2) break;
                    await Task.Delay(25);
                }
            }
            await transaction.CommitAsync();
            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(waiting >= 2, "As duas alterações devem chegar ao bloqueio da linha antes de prosseguir.");
            Assert.Single(results.OfType<OkObjectResult>());
            Assert.Single(results.OfType<ConflictObjectResult>());
            await using var verify = new AppDbContext(options);
            Assert.Contains((await verify.SystemSettings.AsNoTracking().SingleAsync()).ApplicationFeePercent, new[] { 7m, 9m });

            await verify.SystemSettings.ExecuteDeleteAsync();
            await using var barrier = new NpgsqlConnection(testString);
            await barrier.OpenAsync();
            await using (var prepare = new NpgsqlCommand("""
                CREATE FUNCTION fee_test_insert_barrier() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN PERFORM pg_advisory_xact_lock(738191, 4393); RETURN NEW; END $$;
                CREATE TRIGGER fee_test_insert_barrier BEFORE INSERT ON "SystemSettings"
                FOR EACH ROW EXECUTE FUNCTION fee_test_insert_barrier();
                SELECT pg_advisory_lock(738191, 4393);
                """, barrier))
                await prepare.ExecuteNonQueryAsync();
            var createFirst = Update(7m, "missing");
            var createSecond = Update(9m, "missing");
            var insertWaiting = 0;
            await using (var observer = new NpgsqlConnection(testString))
            {
                await observer.OpenAsync();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && !createFirst.IsCompleted && !createSecond.IsCompleted)
                {
                    await using var query = new NpgsqlCommand("SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query ILIKE '%INSERT INTO \"SystemSettings\"%'", observer);
                    insertWaiting = Convert.ToInt32(await query.ExecuteScalarAsync());
                    if (insertWaiting >= 2) break;
                    await Task.Delay(25);
                }
            }
            await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(738191, 4393)", barrier))
                await release.ExecuteScalarAsync();
            var creates = await Task.WhenAll(createFirst, createSecond).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(insertWaiting >= 2, "As duas criações devem consultar a configuração ausente antes de inserir.");
            Assert.Single(creates.OfType<OkObjectResult>());
            Assert.Single(creates.OfType<ConflictObjectResult>());
            Assert.Contains((await verify.SystemSettings.AsNoTracking().SingleAsync()).ApplicationFeePercent, new[] { 7m, 9m });
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ReadingMissingGlobalSettingsReturnsDefaultWithoutWriting()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var controller = new PaymentsController(
            db,
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
                        [new Claim(ClaimTypes.Role, AppRoles.PlatformAdmin)], "Test"))
                }
            }
        };

        var response = Assert.IsType<OkObjectResult>(await controller.GetGlobalSettings());
        using var settings = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(
            response.Value,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        Assert.Equal(5.0m, settings.RootElement.GetProperty("applicationFeePercent").GetDecimal());
        Assert.False(settings.RootElement.TryGetProperty("id", out _));
        Assert.False(settings.RootElement.TryGetProperty("updatedAt", out _));
        Assert.Equal(0, await db.SystemSettings.CountAsync());
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ConditionalFeeUpdateRejectsStaleValueAndPreservesLegacyRequests()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var controller = CreatePlatformAdminController(db);
        controller.Request.Headers["X-Expected-Platform-Fee"] = "missing";
        Assert.IsType<OkObjectResult>(await controller.UpdateGlobalSettings(new SystemSettings { ApplicationFeePercent = 5m }));
        Assert.IsType<ConflictObjectResult>(await controller.UpdateGlobalSettings(new SystemSettings { ApplicationFeePercent = 7m }));
        controller.Request.Headers["X-Expected-Platform-Fee"] = "5";
        Assert.IsType<OkObjectResult>(await controller.UpdateGlobalSettings(new SystemSettings { ApplicationFeePercent = 7m }));
        Assert.IsType<ConflictObjectResult>(await controller.UpdateGlobalSettings(new SystemSettings { ApplicationFeePercent = 9m }));
        Assert.Equal(7m, (await db.SystemSettings.SingleAsync()).ApplicationFeePercent);
        controller.Request.Headers.Remove("X-Expected-Platform-Fee");
        Assert.IsType<OkObjectResult>(await controller.UpdateGlobalSettings(new SystemSettings { ApplicationFeePercent = 9m }));
    }

    [Fact]
    public async Task GlobalFeeRejectsUnsupportedRangeOrPrecisionAndAcceptsValidValue()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var controller = CreatePlatformAdminController(db);

        foreach (var invalidValue in new[] { -0.01m, 0m, 100m, 100.01m, 1.001m })
        {
            var response = await controller.UpdateGlobalSettings(new SystemSettings
            {
                ApplicationFeePercent = invalidValue
            });

            Assert.IsType<BadRequestObjectResult>(response);
            Assert.Equal(0, await db.SystemSettings.CountAsync());
        }

        foreach (var validValue in new[] { 0.01m, 7.25m, 99.99m })
        {
            var validResponse = await controller.UpdateGlobalSettings(new SystemSettings
            {
                ApplicationFeePercent = validValue
            });

            Assert.IsType<OkObjectResult>(validResponse);
            var persisted = await db.SystemSettings.SingleAsync();
            Assert.Equal(validValue, persisted.ApplicationFeePercent);
        }
    }

    private static PaymentsController CreatePlatformAdminController(AppDbContext db) => new(
        db,
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
                    [new Claim(ClaimTypes.Role, AppRoles.PlatformAdmin)], "Test"))
            }
        }
    };
}
