using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace VendingMachines.Api.Services;

public static class DeploymentReadinessEndpoint
{
    public static async Task<IResult> HandleAsync(
        DeploymentReadinessService deploymentReadiness,
        IHostApplicationLifetime lifetime,
        CancellationToken cancellationToken)
    {
        var sourceCommit = ReleaseMetadata.GetSourceCommit(Environment.GetEnvironmentVariable("APP_REVISION"));
        if (lifetime.ApplicationStopping.IsCancellationRequested)
            return Results.Json(new { status = "NotReady", reason = "server_shutting_down", sourceCommit }, statusCode: StatusCodes.Status503ServiceUnavailable);

        try
        {
            using var preflightTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            preflightTimeout.CancelAfter(TimeSpan.FromSeconds(10));

            var blocker = await deploymentReadiness.GetBlockerAsync(preflightTimeout.Token);
            if (blocker != null)
                return Results.Json(new { status = "NotReady", reason = blocker, sourceCommit }, statusCode: StatusCodes.Status503ServiceUnavailable);

            return Results.Ok(new
            {
                status = "Ready",
                sourceCommit
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Results.Json(new { status = "NotReady", reason = "deployment_preflight_failed", sourceCommit }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
