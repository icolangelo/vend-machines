using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VendingMachines.Api.Data;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class ApiReadinessEndpointTests
{
    [Fact]
    public async Task ReadinessDependsOnDatabaseAndMigrationsWithoutWorkerState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var workerHealth = new BackgroundWorkerHealthRegistry();
        Assert.Contains(workerHealth.GetSnapshots(), worker => worker.Status == "NotStarted");
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)");
        foreach (var migration in db.Database.GetMigrations())
            await db.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({0}, {1})", migration, "10.0.0");

        using var lifetime = new TestApplicationLifetime();
        var result = await ApiReadinessEndpoint.HandleAsync(db, lifetime, CancellationToken.None);
        await using var body = new MemoryStream();
        var context = new DefaultHttpContext();
        using var responseServices = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        context.RequestServices = responseServices;
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body);
        Assert.Equal("Ready", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("Connected", json.RootElement.GetProperty("database").GetString());
    }

    [Fact]
    public async Task ReadinessRejectsDatabaseWithPendingMigrations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)");
        var migrations = db.Database.GetMigrations().ToArray();
        Assert.NotEmpty(migrations);
        foreach (var migration in migrations[..^1])
            await db.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({0}, {1})", migration, "10.0.0");

        using var lifetime = new TestApplicationLifetime();
        var result = await ApiReadinessEndpoint.HandleAsync(db, lifetime, CancellationToken.None);
        var context = await ExecuteResultAsync(result);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("database_migrations_pending", await GetJsonStringAsync(context, "reason"));
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(1, json.RootElement.GetProperty("pendingCount").GetInt32());
    }

    [Fact]
    public async Task ReadinessFailsWhenShutdownStartsBeforeCheckingDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        using var lifetime = new TestApplicationLifetime();
        lifetime.BeginStopping();

        var result = await ApiReadinessEndpoint.HandleAsync(db, lifetime, CancellationToken.None);
        var context = await ExecuteResultAsync(result);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("server_shutting_down", await GetJsonStringAsync(context, "reason"));
    }

    internal static async Task<DefaultHttpContext> ExecuteResultAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        using var responseServices = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        context.RequestServices = responseServices;
        await result.ExecuteAsync(context);
        return context;
    }

    internal static async Task<string?> GetJsonStringAsync(HttpContext context, string property)
    {
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        return json.RootElement.GetProperty(property).GetString();
    }

    internal sealed class TestApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => BeginStopping();
        public void BeginStopping() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }
}
