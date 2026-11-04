using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using VendingMachines.Api.Data;

namespace VendingMachines.Api.Services;

public static class ApiReadinessEndpoint
{
    public static async Task<IResult> HandleAsync(
        AppDbContext db,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var sourceCommit = ReleaseMetadata.GetSourceCommit(Environment.GetEnvironmentVariable("APP_REVISION"));
        if (lifetime.ApplicationStopping.IsCancellationRequested)
            return Results.Json(new { status = "NotReady", reason = "server_shutting_down", sourceCommit }, statusCode: StatusCodes.Status503ServiceUnavailable);

        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
                return Results.Json(new { status = "NotReady", reason = "database_unavailable", sourceCommit }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var pendingMigrations = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pendingMigrations.Any())
                return Results.Json(new { status = "NotReady", reason = "database_migrations_pending", pendingCount = pendingMigrations.Count, sourceCommit }, statusCode: StatusCodes.Status503ServiceUnavailable);

            return Results.Ok(new
            {
                status = "Ready",
                mode = configuration.GetValue<bool>("Deployment:PassiveMode") ? "Passive" : "Active",
                database = "Connected",
                sourceCommit,
                timestamp = DateTime.UtcNow
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Results.Json(new { status = "NotReady", reason = "readiness_check_failed", sourceCommit }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
