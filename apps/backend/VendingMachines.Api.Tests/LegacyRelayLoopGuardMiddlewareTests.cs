using System.Text.Json;
using Microsoft.AspNetCore.Http;
using VendingMachines.Api.Middlewares;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class LegacyRelayLoopGuardMiddlewareTests
{
    [Fact]
    public async Task RelayHopReturningToCandidateIsRejectedBeforeEndpointExecution()
    {
        var endpointInvoked = false;
        var middleware = new LegacyRelayLoopGuardMiddleware(_ =>
        {
            endpointInvoked = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Headers[LegacyTelemetryRequestRelay.RelayHopHeaderName] = "1";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.False(endpointInvoked);
        Assert.Equal(StatusCodes.Status508LoopDetected, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var payload = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("legacy_relay_loop_blocked", payload.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task OrdinaryRequestContinuesToEndpoint()
    {
        var endpointInvoked = false;
        var middleware = new LegacyRelayLoopGuardMiddleware(_ =>
        {
            endpointInvoked = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(new DefaultHttpContext());

        Assert.True(endpointInvoked);
    }
}
