using Microsoft.AspNetCore.Http;

namespace VendingMachines.Api.Middlewares;

/// <summary>
/// Restricts a passive candidate instance to process and database health probes.
/// No application or device endpoint is reachable while this mode is enabled.
/// </summary>
public sealed class PassiveDeploymentModeMiddleware(RequestDelegate next, bool enabled)
{
    private static readonly HashSet<string> AllowedProbePaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/",
        "/health",
        "/health/ready"
    };

    public async Task InvokeAsync(HttpContext context)
    {
        if (!enabled || (!context.WebSockets.IsWebSocketRequest &&
                         IsAllowedProbe(context.Request.Method, context.Request.Path)))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new
        {
            status = "Passive",
            reason = "passive_mode_endpoint_disabled"
        }, context.RequestAborted);
    }

    internal static bool IsAllowedProbe(string method, PathString path) =>
        (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) &&
        AllowedProbePaths.Contains(path.Value ?? string.Empty);
}
