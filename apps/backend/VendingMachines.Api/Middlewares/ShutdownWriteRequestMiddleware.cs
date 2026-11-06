using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Middlewares;

public sealed class ShutdownWriteRequestMiddleware(
    RequestDelegate next,
    IHostApplicationLifetime lifetime,
    ShutdownWriteRequestGate writeRequestGate)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsWriteRequest(context.Request.Method))
        {
            await next(context);
            return;
        }

        var admission = writeRequestGate.TryBeginWriteRequest();
        if (admission is null || lifetime.ApplicationStopping.IsCancellationRequested)
        {
            admission?.Dispose();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "5";
            await context.Response.WriteAsJsonAsync(
                new
                {
                    error = "server_shutting_down",
                    message = "O servidor está sendo atualizado. Tente novamente em instantes."
                },
                context.RequestAborted);
            return;
        }

        using (admission)
            await next(context);
    }

    private static bool IsWriteRequest(string method) =>
        HttpMethods.IsPost(method) ||
        HttpMethods.IsPut(method) ||
        HttpMethods.IsPatch(method) ||
        HttpMethods.IsDelete(method);
}
