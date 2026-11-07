using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using VendingMachines.Api.Middlewares;
using VendingMachines.Api.Services;
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
        Assert.Equal("max-age=31536000", context.Response.Headers["Strict-Transport-Security"].ToString());
        Assert.Equal("default-src 'none'; frame-ancestors 'none'; base-uri 'none'", context.Response.Headers["Content-Security-Policy"].ToString());
        Assert.Equal("no-store", context.Response.Headers["Cache-Control"].ToString());
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
        Assert.False(context.Response.Headers.ContainsKey("Cache-Control"));
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("max-age=31536000", context.Response.Headers["Strict-Transport-Security"].ToString());
    }

    [Fact]
    public async Task KestrelResponsesDoNotExposeServerProductHeader()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            SecurityHeadersConfiguration.ConfigureKestrel(options);
            options.Listen(IPAddress.Loopback, 0);
        });

        await using var app = builder.Build();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.MapGet("/api/health-check", () => Results.Ok());
        await app.StartAsync();

        try
        {
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.Single();
            Assert.NotNull(address);

            using var client = new HttpClient();
            using var response = await client.GetAsync(new Uri(new Uri(address), "/api/health-check"));

            Assert.True(response.IsSuccessStatusCode);
            Assert.False(response.Headers.Contains("Server"));
            Assert.Equal("max-age=31536000", response.Headers.GetValues("Strict-Transport-Security").Single());
            Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
