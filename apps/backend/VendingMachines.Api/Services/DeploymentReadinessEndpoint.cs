using Microsoft.AspNetCore.Http;

namespace VendingMachines.Api.Services;

public static class DeploymentReadinessEndpoint
{
    public static async Task<IResult> HandleAsync(
        DeploymentReadinessService deploymentReadiness,
        CancellationToken cancellationToken)
    {
        try
        {
            var blocker = await deploymentReadiness.GetBlockerAsync(cancellationToken);
            if (blocker != null)
                return Results.Json(new { status = "NotReady", reason = blocker }, statusCode: StatusCodes.Status503ServiceUnavailable);

            return Results.Ok(new { status = "Ready" });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Results.Json(new { status = "NotReady", reason = "deployment_preflight_failed" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
