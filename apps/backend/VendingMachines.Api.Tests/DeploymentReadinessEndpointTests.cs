using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VendingMachines.Api.Data;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class DeploymentReadinessEndpointTests
{
    [Fact]
    public async Task DatabaseFailureReturnsGenericNotReadyResponseWithoutDetails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"MachineSessions\"");

        var readiness = new DeploymentReadinessService(db);
        var result = await DeploymentReadinessEndpoint.HandleAsync(readiness, CancellationToken.None);
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

    private static async Task<string> ReadBodyAsync(Stream body)
    {
        body.Position = 0;
        using var reader = new StreamReader(body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
