using VendingMachines.Api.Services;

namespace VendingMachines.Api.Middlewares;

public sealed class WebSocketMiddleware
{
    private readonly RequestDelegate _next;

    public WebSocketMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        TelemetryConnectionManager connections,
        TelemetryPanelHub panelHub,
        WebSocketTicketService ticketService)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await _next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/telemetria"))
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await connections.HandleDeviceAsync(socket, context.RequestAborted);
            return;
        }

        if (context.Request.Path == "/ws/telemetry")
        {
            var token = context.Request.Query["ticket"].ToString();
            if (string.IsNullOrWhiteSpace(token) || !ticketService.TryConsume(token, out var ticket))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await panelHub.HandleAsync(socket, ticket, context.RequestAborted);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }
}
