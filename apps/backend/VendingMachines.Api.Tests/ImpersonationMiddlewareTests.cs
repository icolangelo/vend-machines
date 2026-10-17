using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using VendingMachines.Api.Middlewares;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class ImpersonationMiddlewareTests
{
    [Fact]
    public async Task CompanyAdminCannotInjectOrRetainImpersonationClaim()
    {
        var ownCompanyId = Guid.NewGuid();
        var otherCompanyId = Guid.NewGuid();
        var identity = new ClaimsIdentity(
        [
            new Claim("company_id", ownCompanyId.ToString()),
            new Claim(ClaimTypes.Role, AppRoles.Admin),
            new Claim(ImpersonationMiddleware.ImpersonatedCompanyIdClaim, otherCompanyId.ToString())
        ], "test");
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };
        context.Request.Headers["X-Impersonate-Company-Id"] = otherCompanyId.ToString();

        await InvokeAsync(context);

        Assert.Empty(context.User.FindAll(ImpersonationMiddleware.ImpersonatedCompanyIdClaim));
        Assert.Equal(ownCompanyId.ToString(), context.User.FindFirst("company_id")?.Value);
    }

    [Fact]
    public async Task PlatformAdminHeaderReplacesAnyPriorImpersonationClaim()
    {
        var staleCompanyId = Guid.NewGuid();
        var requestedCompanyId = Guid.NewGuid();
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, AppRoles.PlatformAdmin),
            new Claim(ImpersonationMiddleware.ImpersonatedCompanyIdClaim, staleCompanyId.ToString())
        ], "test");
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };
        context.Request.Headers["X-Impersonate-Company-Id"] = requestedCompanyId.ToString();

        await InvokeAsync(context);

        var claim = Assert.Single(context.User.FindAll(ImpersonationMiddleware.ImpersonatedCompanyIdClaim));
        Assert.Equal(requestedCompanyId.ToString(), claim.Value);
    }

    [Fact]
    public async Task InvalidPlatformAdminHeaderClearsPriorImpersonationClaim()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, AppRoles.PlatformAdmin),
            new Claim(ImpersonationMiddleware.ImpersonatedCompanyIdClaim, Guid.NewGuid().ToString())
        ], "test");
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };
        context.Request.Headers["X-Impersonate-Company-Id"] = "invalid-guid";

        await InvokeAsync(context);

        Assert.Empty(context.User.FindAll(ImpersonationMiddleware.ImpersonatedCompanyIdClaim));
    }

    private static Task InvokeAsync(HttpContext context)
    {
        var middleware = new ImpersonationMiddleware(_ => Task.CompletedTask);
        return middleware.InvokeAsync(context);
    }
}
