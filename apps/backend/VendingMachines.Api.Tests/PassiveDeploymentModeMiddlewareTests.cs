using Microsoft.AspNetCore.Http;
using VendingMachines.Api.Middlewares;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class PassiveDeploymentModeMiddlewareTests
{
    [Theory]
    [InlineData("GET", "/telemetria")]
    [InlineData("GET", "/api/machines")]
    [InlineData("GET", "/health/deploy-ready")]
    [InlineData("GET", "/swagger")]
    [InlineData("POST", "/health")]
    [InlineData("POST", "/api/auth/login")]
    public async Task PassiveModeRejectsApplicationAndDeviceEndpoints(string method, string path)
    {
        var nextWasCalled = false;
        var middleware = new PassiveDeploymentModeMiddleware(_ =>
        {
            nextWasCalled = true;
            return Task.CompletedTask;
        }, enabled: true);
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.False(nextWasCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/health")]
    [InlineData("GET", "/health/ready")]
    [InlineData("HEAD", "/health")]
    [InlineData("GET", "/")]
    public async Task PassiveModeAllowsOnlyReadinessAndLivenessProbes(string method, string path)
    {
        var nextWasCalled = false;
        var middleware = new PassiveDeploymentModeMiddleware(_ =>
        {
            nextWasCalled = true;
            return Task.CompletedTask;
        }, enabled: true);
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        Assert.True(nextWasCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task DisabledPassiveModeLeavesRequestPipelineUnchanged()
    {
        var nextWasCalled = false;
        var middleware = new PassiveDeploymentModeMiddleware(_ =>
        {
            nextWasCalled = true;
            return Task.CompletedTask;
        }, enabled: false);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/machines";

        await middleware.InvokeAsync(context);

        Assert.True(nextWasCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }
}
