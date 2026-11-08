using VendingMachines.Api.Services;

namespace VendingMachines.Api.Middlewares;

/// <summary>
/// Rejects a relay request that has returned to a candidate API instead of reaching
/// the previous process. The previous binary ignores this private hop marker.
/// </summary>
public sealed class LegacyRelayLoopGuardMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey(LegacyTelemetryRequestRelay.RelayHopHeaderName) ||
            context.Request.Headers.ContainsKey(LegacyTelemetryRequestRelay.WebhookRelayHopHeaderName))
        {
            context.Response.StatusCode = StatusCodes.Status508LoopDetected;
            await context.Response.WriteAsJsonAsync(new { code = "legacy_relay_loop_blocked" });
            return;
        }

        await next(context);
    }
}
