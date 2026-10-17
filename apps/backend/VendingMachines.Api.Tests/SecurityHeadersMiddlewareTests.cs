using Microsoft.AspNetCore.Http;
using VendingMachines.Api.Middlewares;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class SecurityHeadersMiddlewareTests
{
    [Fact]
    public async Task ApiResponsesIncludeSecurityHeadersAndRestrictiveContentPolicy()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/machines";
        context.Response.Body = new MemoryStream();
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"].ToString());
        Assert.Equal("strict-origin-when-cross-origin", context.Response.Headers["Referrer-Policy"].ToString());
        Assert.Equal("camera=(), geolocation=(), microphone=()", context.Response.Headers["Permissions-Policy"].ToString());
        Assert.Equal("default-src 'none'; frame-ancestors 'none'; base-uri 'none'", context.Response.Headers["Content-Security-Policy"].ToString());
    }

    [Fact]
    public async Task MiddlewareDoesNotAddApiContentPolicyToSwaggerDocuments()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/swagger/v1/swagger.json";
        context.Response.Body = new MemoryStream();
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.False(context.Response.Headers.ContainsKey("Content-Security-Policy"));
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
    }
}
